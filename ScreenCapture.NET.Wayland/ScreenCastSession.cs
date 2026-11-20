using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace ScreenCapture.NET;

/// <summary>
/// A screen cast session of the xdg-desktop-portal (<c>org.freedesktop.portal.ScreenCast</c>) on its own connection to the session bus.
/// </summary>
/// <remarks>
/// The portal ends the sessions of a connection that closes, so disposing the session (and with it the connection) always ends it.
/// </remarks>
internal sealed class ScreenCastSession : IDisposable
{
    #region Constants

    internal const string PORTAL_NAME = "org.freedesktop.portal.Desktop";
    internal const string PORTAL_PATH = "/org/freedesktop/portal/desktop";
    internal const string SCREEN_CAST_INTERFACE = "org.freedesktop.portal.ScreenCast";
    internal const string REQUEST_INTERFACE = "org.freedesktop.portal.Request";
    internal const string SESSION_INTERFACE = "org.freedesktop.portal.Session";

    internal const uint RESPONSE_SUCCESS = 0;
    internal const uint RESPONSE_CANCELLED = 1;

    private const uint SOURCE_TYPE_MONITOR = 1;

    internal const uint CURSOR_MODE_HIDDEN = 1;
    internal const uint CURSOR_MODE_EMBEDDED = 2;

    private const string PROPERTIES_INTERFACE = "org.freedesktop.DBus.Properties";

    // The version of the interface that has persist_mode and restore_token.
    internal const uint RESTORE_VERSION = 4;

    // Disposing waits this long for the portal to confirm the end of the session; the connection is closed afterwards anyway.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    private static int _lastToken;

    #endregion

    #region Properties & Fields

    private readonly DBusConnection _connection;
    private Task _disconnected = Task.CompletedTask;
    private IDisposable? _closedWatch;
    private volatile string? _sessionPath;
    private volatile bool _isClosed;
    private volatile bool _isStarted;
    private int _isDisposed;

    // Guards the PipeWire connection, created by the first capture.
    private readonly Lock _coreLock = new();
    private volatile PipeWireCore? _core;

    /// <summary>
    /// Gets the streams the user shared; empty until the session is started.
    /// </summary>
    public IReadOnlyList<PortalStream> Streams { get; private set; } = [];

    /// <summary>
    /// Gets a value indicating whether the session ended: the portal closed it (e.g. the user stopped sharing), the connection to the session bus closed,
    /// or the connection to PipeWire broke (the nodes of the session are gone with it, e.g. when the daemon restarted).
    /// </summary>
    public bool IsClosed => _isClosed || _disconnected.IsCompleted || (_core?.IsDead ?? false);

    /// <summary>
    /// Gets a value indicating whether the session was started (the user shared the <see cref="Streams"/>).
    /// </summary>
    public bool IsStarted => _isStarted;

    /// <summary>
    /// Gets the token the portal returned to restore the session later; <c>null</c> if it returned none (e.g. without a persist mode, or before version 4).
    /// </summary>
    public string? RestoreToken { get; private set; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new session on the session bus. It's started with <see cref="StartAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no session bus.</exception>
    public ScreenCastSession()
    {
        string address = DBusAddress.Session ?? throw new InvalidOperationException("There's no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS isn't set).");
        if (address.Length == 0) throw new InvalidOperationException("There's no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS isn't set).");

        _connection = new DBusConnection(address);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Creates the session, lets the user choose the monitors to share and starts sharing them. Waits until the user answered the dialog of the compositor.
    /// </summary>
    /// <param name="options">The options of the session.</param>
    /// <param name="restoreToken">The token of the session to restore; <c>null</c> for none.</param>
    /// <exception cref="InvalidOperationException">The portal isn't available, the user cancelled the dialog or the portal ended the session.</exception>
    public async Task StartAsync(WaylandSessionOptions options, string? restoreToken)
    {
        try
        {
            await _connection.ConnectAsync().ConfigureAwait(false);
            _disconnected = _connection.DisconnectedAsync();

            Dictionary<string, VariantValue> created = await RequestAsync("CreateSession", (ref MessageWriter writer) => WriteOption(ref writer, "session_handle_token", CreateToken())).ConfigureAwait(false);
            string sessionPath = ReadSessionHandle(created);
            _closedWatch = await _connection.WatchSignalAsync(PORTAL_NAME, sessionPath, SESSION_INTERFACE, "Closed", _ => _isClosed = true, ObserverFlags.None, false, null).ConfigureAwait(false);
            _sessionPath = sessionPath;

            uint? cursorMode = GetCursorMode(options, await ReadPropertyAsync("AvailableCursorModes").ConfigureAwait(false));

            // Before version 4 the portal can't restore sessions (the options would be ignored anyway).
            bool canRestore = await ReadPropertyAsync("version").ConfigureAwait(false) >= RESTORE_VERSION;
            await RequestAsync("SelectSources", (ref MessageWriter writer) =>
            {
                WriteOption(ref writer, "types", SOURCE_TYPE_MONITOR);
                WriteOption(ref writer, "multiple", true);
                if (cursorMode is { } mode)
                    WriteOption(ref writer, "cursor_mode", mode);

                if (canRestore && (options.PersistMode != WaylandPersistMode.None))
                    WriteOption(ref writer, "persist_mode", (uint)options.PersistMode);
                if (canRestore && !string.IsNullOrEmpty(restoreToken))
                    WriteOption(ref writer, "restore_token", restoreToken);
            }, sessionPath).ConfigureAwait(false);

            Dictionary<string, VariantValue> started = await RequestAsync("Start", null, sessionPath, parentWindow: string.Empty).ConfigureAwait(false);
            Streams = PortalStream.ReadStreams(started);
            RestoreToken = started.TryGetValue("restore_token", out VariantValue token) && (token.Type == VariantValueType.String) && (token.GetString().Length > 0) ? token.GetString() : null;
            _isStarted = true;
        }
        catch (DBusExceptionBase ex)
        {
            throw new InvalidOperationException($"The screen cast portal (xdg-desktop-portal) isn't available: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the connection to PipeWire through which the streams of the session are received; the first call opens it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    /// <exception cref="InvalidOperationException">The portal didn't open the connection, or libpipewire couldn't connect.</exception>
    public PipeWireCore GetPipeWireCore()
    {
        lock (_coreLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            if (_core != null) return _core;

            string sessionPath = _sessionPath ?? throw new InvalidOperationException("The screen cast session wasn't started.");

            // On the thread pool: waiting on it here can't deadlock with a synchronization context of the caller.
            SafeFileHandle socket = Task.Run(() => OpenPipeWireRemoteAsync(sessionPath)).GetAwaiter().GetResult();
            _core = PipeWireCore.Connect(socket);
            return _core;
        }
    }

    // The socket only shows PipeWire the nodes of this session.
    private async Task<SafeFileHandle> OpenPipeWireRemoteAsync(string sessionPath)
    {
        MessageWriter writer = _connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(PORTAL_NAME, PORTAL_PATH, SCREEN_CAST_INTERFACE, "OpenPipeWireRemote", "oa{sv}", MessageFlags.None);
            writer.WriteObjectPath(sessionPath);
            ArrayStart options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(options);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        try
        {
            return await _connection.CallMethodAsync(call, (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>(), null).ConfigureAwait(false);
        }
        catch (DBusExceptionBase ex)
        {
            throw new InvalidOperationException($"The screen cast portal didn't open the PipeWire connection: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets the cursor mode to ask for: only one the portal offers (asking for another one ends the session); <c>null</c> to ask for none (the portal hides the pointer then).
    /// </summary>
    /// <param name="options">The options of the session.</param>
    /// <param name="availableCursorModes">The <c>AvailableCursorModes</c> of the portal (0 before version 2 of the interface, which has no cursor modes).</param>
    internal static uint? GetCursorMode(WaylandSessionOptions options, uint availableCursorModes)
    {
        uint mode = options.DrawCursor ? CURSOR_MODE_EMBEDDED : CURSOR_MODE_HIDDEN;
        return (availableCursorModes & mode) != 0 ? mode : null;
    }

    /// <summary>
    /// Reads the version of the ScreenCast interface of the portal on the session bus, without starting a session.
    /// </summary>
    /// <returns>The version; 0 if there's no session bus, no portal, or a portal without the ScreenCast interface.</returns>
    public static async Task<uint> ReadPortalVersionAsync()
    {
        string? address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return 0;

        using DBusConnection connection = new(address);
        await connection.ConnectAsync().ConfigureAwait(false);
        return await ReadPropertyAsync(connection, "version").ConfigureAwait(false);
    }

    private Task<uint> ReadPropertyAsync(string name) => ReadPropertyAsync(_connection, name);

    // A property of the ScreenCast interface; 0 if it doesn't exist (e.g. AvailableCursorModes before version 2, or no ScreenCast interface at all).
    private static async Task<uint> ReadPropertyAsync(DBusConnection connection, string name)
    {
        MessageWriter writer = connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(PORTAL_NAME, PORTAL_PATH, PROPERTIES_INTERFACE, "Get", "ss", MessageFlags.None);
            writer.WriteString(SCREEN_CAST_INTERFACE);
            writer.WriteString(name);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        try
        {
            VariantValue modes = await connection.CallMethodAsync(call, (message, _) => message.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            return modes.Type == VariantValueType.UInt32 ? modes.GetUInt32() : 0;
        }
        catch (DBusErrorReplyException)
        {
            return 0;
        }
    }

    // A method of the ScreenCast interface that answers with a request and later with the Response signal of the request.
    private async Task<Dictionary<string, VariantValue>> RequestAsync(string method, OptionsWriter? writeOptions, string? sessionPath = null, string? parentWindow = null)
    {
        string token = CreateToken();
        string requestPath = GetRequestPath(_connection.UniqueName ?? string.Empty, token);

        // Watched before the call, so a response sent before the reply isn't missed.
        TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable watch = await _connection.WatchSignalAsync(PORTAL_NAME, requestPath, REQUEST_INTERFACE, "Response", ReadResponse,
                                                                     notification =>
                                                                     {
                                                                         if (notification.HasValue) response.TrySetResult(notification.Value);
                                                                         else if (notification.CompletionReason != null) response.TrySetException(notification.CompletionReason);
                                                                     }, ObserverFlags.None, false, null).ConfigureAwait(false);

        string handle = await _connection.CallMethodAsync(CreateRequestCall(method, token, writeOptions, sessionPath, parentWindow), (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null)
                                         .ConfigureAwait(false);

        // Portals before xdg-desktop-portal 0.9 (2017) ignore the handle token; their response could be sent before it's watched.
        if (!string.Equals(handle, requestPath, StringComparison.Ordinal))
            throw new InvalidOperationException($"The screen cast portal answered {method} with the unexpected request {handle} (xdg-desktop-portal older than 0.9?).");

        // The response of a dialog the user doesn't answer never comes; the disconnect (e.g. by Dispose) ends the wait.
        await Task.WhenAny(response.Task, _disconnected).ConfigureAwait(false);
        if (!response.Task.IsCompleted)
            throw new DBusConnectionClosedException($"The connection to the session bus closed while waiting for the response to {method}.");

        (uint code, Dictionary<string, VariantValue> results) = await response.Task.ConfigureAwait(false);
        if (code != RESPONSE_SUCCESS) throw CreateResponseException(method, code);

        return results;
    }

    private MessageBuffer CreateRequestCall(string method, string token, OptionsWriter? writeOptions, string? sessionPath, string? parentWindow)
    {
        string signature = (sessionPath != null ? "o" : string.Empty) + (parentWindow != null ? "s" : string.Empty) + "a{sv}";

        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(PORTAL_NAME, PORTAL_PATH, SCREEN_CAST_INTERFACE, method, signature, MessageFlags.None);
            if (sessionPath != null) writer.WriteObjectPath(sessionPath);
            if (parentWindow != null) writer.WriteString(parentWindow);

            ArrayStart options = writer.WriteDictionaryStart();
            WriteOption(ref writer, "handle_token", token);
            writeOptions?.Invoke(ref writer);
            writer.WriteDictionaryEnd(options);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteOption(ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    private static void WriteOption(ref MessageWriter writer, string key, uint value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantUInt32(value);
    }

    private static void WriteOption(ref MessageWriter writer, string key, bool value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantBool(value);
    }

    private static (uint Code, Dictionary<string, VariantValue> Results) ReadResponse(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
    }

    // Version 1 of the interface sent the handle as a string, later ones as an object path.
    private static string ReadSessionHandle(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("session_handle", out VariantValue handle))
            throw new InvalidOperationException("The screen cast portal created a session without a handle.");

        return handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
    }

    /// <summary>
    /// Gets the path of the request with the given token: the portal puts it under the unique name of the caller, without the leading ':' and with '_' for '.'.
    /// </summary>
    internal static string GetRequestPath(string uniqueName, string token)
        => $"{PORTAL_PATH}/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";

    // Tokens are unique per connection and may only contain [A-Za-z0-9_].
    private static string CreateToken() => $"screencapture_net_{(uint)Interlocked.Increment(ref _lastToken)}";

    internal static InvalidOperationException CreateResponseException(string method, uint code)
        => code == RESPONSE_CANCELLED
               ? new InvalidOperationException($"The user cancelled the screen cast ({method}).")
               : new InvalidOperationException($"The screen cast portal ended the session ({method} failed with response {code}).");

    /// <summary>
    /// Closes the PipeWire connection (destroying the streams of the captures), ends the session and closes the connection to the bus.
    /// Can be called while <see cref="StartAsync"/> waits, which then fails.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        // The streams of the captures first: they're destroyed with it.
        lock (_coreLock)
        {
            _core?.Dispose();
            _core = null;
        }

        string? sessionPath = _sessionPath;
        if ((sessionPath != null) && !IsClosed)
        {
            try
            {
                MessageWriter writer = _connection.GetMessageWriter();
                MessageBuffer close;
                try
                {
                    writer.WriteMethodCallHeader(PORTAL_NAME, sessionPath, SESSION_INTERFACE, "Close", null, MessageFlags.None);
                    close = writer.CreateMessage();
                }
                finally
                {
                    writer.Dispose();
                }

                _connection.CallMethodAsync(close).Wait(CloseTimeout);
            }
            catch (Exception)
            {
                // The connection closes anyway, which ends the session too.
            }
        }

        _closedWatch?.Dispose();
        _connection.Dispose();
    }

    #endregion

    // MessageWriter is a ref struct: passed by value (e.g. as an Action<MessageWriter>) the options would be written into a copy and the bus would drop the connection.
    private delegate void OptionsWriter(ref MessageWriter writer);
}
