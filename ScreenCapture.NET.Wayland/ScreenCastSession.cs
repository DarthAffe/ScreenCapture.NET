using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private int _isDisposed;

    /// <summary>
    /// Gets the streams the user shared; empty until the session is started.
    /// </summary>
    public IReadOnlyList<PortalStream> Streams { get; private set; } = [];

    /// <summary>
    /// Gets a value indicating whether the session ended: the portal closed it (e.g. the user stopped sharing) or the connection to the session bus closed.
    /// </summary>
    public bool IsClosed => _isClosed || _disconnected.IsCompleted;

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
    /// <exception cref="InvalidOperationException">The portal isn't available, the user cancelled the dialog or the portal ended the session.</exception>
    public async Task StartAsync()
    {
        try
        {
            await _connection.ConnectAsync().ConfigureAwait(false);
            _disconnected = _connection.DisconnectedAsync();

            Dictionary<string, VariantValue> created = await RequestAsync("CreateSession", (ref MessageWriter writer) => WriteOption(ref writer, "session_handle_token", CreateToken())).ConfigureAwait(false);
            string sessionPath = ReadSessionHandle(created);
            _closedWatch = await _connection.WatchSignalAsync(PORTAL_NAME, sessionPath, SESSION_INTERFACE, "Closed", _ => _isClosed = true, ObserverFlags.None, false, null).ConfigureAwait(false);
            _sessionPath = sessionPath;

            await RequestAsync("SelectSources", (ref MessageWriter writer) =>
            {
                WriteOption(ref writer, "types", SOURCE_TYPE_MONITOR);
                WriteOption(ref writer, "multiple", true);
            }, sessionPath).ConfigureAwait(false);

            Dictionary<string, VariantValue> started = await RequestAsync("Start", null, sessionPath, parentWindow: string.Empty).ConfigureAwait(false);
            Streams = PortalStream.ReadStreams(started);
        }
        catch (DBusExceptionBase ex)
        {
            throw new InvalidOperationException($"The screen cast portal (xdg-desktop-portal) isn't available: {ex.Message}", ex);
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
    /// Ends the session and closes the connection. Can be called while <see cref="StartAsync"/> waits, which then fails.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

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
