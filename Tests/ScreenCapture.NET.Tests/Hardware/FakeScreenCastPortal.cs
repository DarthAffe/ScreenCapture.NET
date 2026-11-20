using System.Net.Sockets;
using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// A ScreenCast portal on the <see cref="PrivateSessionBus"/> that answers like xdg-desktop-portal: requests under the path of their handle token,
/// responses as <c>Response</c> signals of the requests, the session under the path of its token. The "dialog" is answered with <see cref="StartResponse"/>.
/// <c>OpenPipeWireRemote</c> hands out a socket of the PipeWire daemon of the user (unlike the real portal's, it isn't limited to the nodes of the session).
/// Disposing it removes the portal from the bus.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class FakeScreenCastPortal : IPathMethodHandler, IDisposable
{
    #region Constants

    private const string REQUEST_INTERFACE = "org.freedesktop.portal.Request";
    private const string SESSION_INTERFACE = "org.freedesktop.portal.Session";
    private const string SCREEN_CAST_INTERFACE = "org.freedesktop.portal.ScreenCast";

    #endregion

    #region Properties & Fields

    private readonly DBusConnection _connection;
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];
    private readonly HashSet<string> _sessions = [];
    private Dictionary<string, VariantValue>? _selectSourcesOptions;

    // Kept open until the portal is disposed: the reply that passes them on is sent asynchronously.
    private readonly List<Socket> _sockets = [];

    public string Path => "/org/freedesktop/portal/desktop";
    public bool HandlesChildPaths => true;

    /// <summary>
    /// Gets the streams the user "shares".
    /// </summary>
    public List<FakeStream> Streams { get; } = [new FakeStream(42, Size: (1920, 1080))];

    /// <summary>
    /// Gets or sets the response code of <c>Start</c> (0 = shared, 1 = cancelled, 2 = failed).
    /// </summary>
    public uint StartResponse { get; set; }

    /// <summary>
    /// Gets or sets what the response of <c>Start</c> waits for (the user answering the dialog).
    /// </summary>
    public Task StartAnswered { get; set; } = Task.CompletedTask;

    /// <summary>
    /// Gets or sets a value indicating whether responses are sent before the reply to the call (a race a client has to handle).
    /// </summary>
    public bool RespondBeforeReply { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the session handle is sent as an object path (like the specification says) instead of a string (like KDE does).
    /// </summary>
    public bool SessionHandleAsObjectPath { get; set; }

    /// <summary>
    /// Gets the methods called so far, in order (<c>CreateSession</c>, <c>SelectSources</c>, <c>Start</c>, <c>OpenPipeWireRemote</c>, <c>Close</c>).
    /// </summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
                return [.. _calls];
        }
    }

    /// <summary>
    /// Gets the options of the last <c>SelectSources</c>.
    /// </summary>
    public Dictionary<string, VariantValue>? SelectSourcesOptions
    {
        get
        {
            lock (_lock)
                return _selectSourcesOptions;
        }
    }

    #endregion

    #region Constructors

    private FakeScreenCastPortal(DBusConnection connection)
    {
        this._connection = connection;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Starts the portal on the <see cref="PrivateSessionBus"/>.
    /// </summary>
    public static FakeScreenCastPortal Start()
    {
        DBusConnection connection = new(PrivateSessionBus.Address);
        FakeScreenCastPortal portal = new(connection);
        try
        {
            connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
            connection.AddMethodHandler(portal);

            // The bus releases the name of the portal of the previous test once it handled its disconnect.
            Assert.IsTrue(SpinWait.SpinUntil(() => connection.TryRequestNameAsync("org.freedesktop.portal.Desktop", RequestNameOptions.None).GetAwaiter().GetResult(), TimeSpan.FromSeconds(5)),
                          "The portal name is still owned.");

            return portal;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int CountCalls(string method) => Calls.Count(call => call == method);

    /// <summary>
    /// Ends all sessions like the portal does when the user stops sharing (<c>Session.Closed</c>).
    /// </summary>
    public void CloseSessions()
    {
        string[] sessions;
        lock (_lock)
        {
            sessions = [.. _sessions];
            _sessions.Clear();
        }

        foreach (string session in sessions)
        {
            MessageWriter writer = _connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(null, session, SESSION_INTERFACE, "Closed", "a{sv}");
                writer.WriteDictionary(new Dictionary<string, VariantValue>());
                _connection.TrySendMessage(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        Message request = context.Request;
        string method = request.MemberAsString ?? string.Empty;

        switch (request.InterfaceAsString)
        {
            case SCREEN_CAST_INTERFACE when method == "CreateSession":
            {
                Dictionary<string, VariantValue> options = context.Request.GetBodyReader().ReadDictionaryOfStringToVariantValue();
                string session = $"{Path}/session/{GetSender(request)}/{options["session_handle_token"].GetString()}";
                lock (_lock)
                {
                    _calls.Add(method);
                    _sessions.Add(session);
                }

                Respond(context, options, 0, session, null);
                break;
            }

            case SCREEN_CAST_INTERFACE when method == "SelectSources":
            {
                Reader reader = request.GetBodyReader();
                string session = reader.ReadObjectPathAsString();
                Dictionary<string, VariantValue> options = reader.ReadDictionaryOfStringToVariantValue();
                if (!IsSession(context, session)) break;

                lock (_lock)
                {
                    _calls.Add(method);
                    _selectSourcesOptions = options;
                }

                Respond(context, options, 0, null, null);
                break;
            }

            case SCREEN_CAST_INTERFACE when method == "Start":
            {
                Reader reader = request.GetBodyReader();
                string session = reader.ReadObjectPathAsString();
                _ = reader.ReadString(); // parent window
                Dictionary<string, VariantValue> options = reader.ReadDictionaryOfStringToVariantValue();
                if (!IsSession(context, session)) break;

                lock (_lock)
                    _calls.Add(method);

                Respond(context, options, StartResponse, null, [.. Streams], StartAnswered);
                break;
            }

            case SCREEN_CAST_INTERFACE when method == "OpenPipeWireRemote":
            {
                string session = request.GetBodyReader().ReadObjectPathAsString();
                if (!IsSession(context, session)) break;

                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(PipeWireVideoSource.GetDaemonSocket()));
                lock (_lock)
                {
                    _calls.Add(method);
                    _sockets.Add(socket);
                }

                MessageWriter writer = context.CreateReplyWriter("h");
                try
                {
                    writer.WriteHandle(socket.SafeHandle);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case SESSION_INTERFACE when method == "Close":
            {
                lock (_lock)
                {
                    _calls.Add(method);
                    _sessions.Remove(request.PathAsString ?? string.Empty);
                }

                MessageWriter writer = context.CreateReplyWriter(null);
                try
                {
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return ValueTask.CompletedTask;
    }

    private bool IsSession(MethodContext context, string session)
    {
        lock (_lock)
            if (_sessions.Contains(session))
                return true;

        context.ReplyError("org.freedesktop.portal.Error.NotFound", $"No session {session}.");
        return false;
    }

    // Replies with the request and sends its response, like the portal does once the backend answered (after `answered`; ignored with RespondBeforeReply).
    private void Respond(MethodContext context, Dictionary<string, VariantValue> options, uint code, string? session, List<FakeStream>? streams, Task? answered = null)
    {
        string requestPath = $"{Path}/request/{GetSender(context.Request)}/{options["handle_token"].GetString()}";

        if (RespondBeforeReply)
            SendResponse(requestPath, code, session, streams);

        MessageWriter writer = context.CreateReplyWriter("o");
        try
        {
            writer.WriteObjectPath(requestPath);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }

        if (!RespondBeforeReply)
            _ = (answered ?? Task.CompletedTask).ContinueWith(_ => SendResponse(requestPath, code, session, streams), TaskScheduler.Default);
    }

    private void SendResponse(string requestPath, uint code, string? session, List<FakeStream>? streams)
    {
        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null, requestPath, REQUEST_INTERFACE, "Response", "ua{sv}");
            writer.WriteUInt32(code);

            ArrayStart results = writer.WriteDictionaryStart();
            if (session != null)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("session_handle");
                if (SessionHandleAsObjectPath) writer.WriteVariantObjectPath(session);
                else writer.WriteVariantString(session);
            }

            if (streams != null)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("streams");
                writer.WriteSignature("a(ua{sv})");
                ArrayStart array = writer.WriteArrayStart(DBusType.Struct);
                foreach (FakeStream stream in streams)
                {
                    writer.WriteStructureStart();
                    writer.WriteUInt32(stream.NodeId);

                    ArrayStart properties = writer.WriteDictionaryStart();
                    if (stream.Id != null)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString("id");
                        writer.WriteVariantString(stream.Id);
                    }

                    if (stream.Position is (int x, int y))
                        WritePair(ref writer, "position", x, y);

                    if (stream.Size is (int width, int height))
                        WritePair(ref writer, "size", width, height);

                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("source_type");
                    writer.WriteVariantUInt32(1);

                    writer.WriteDictionaryEnd(properties);
                }

                writer.WriteArrayEnd(array);
            }

            writer.WriteDictionaryEnd(results);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WritePair(ref MessageWriter writer, string key, int first, int second)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteSignature("(ii)");
        writer.WriteStructureStart();
        writer.WriteInt32(first);
        writer.WriteInt32(second);
    }

    // The unique name of the caller without the ':' and with '_' for '.' (computed here on its own, not with the code under test).
    private static string GetSender(Message request) => request.SenderAsString![1..].Replace('.', '_');

    public void Dispose()
    {
        _connection.Dispose();

        lock (_lock)
            foreach (Socket socket in _sockets)
                socket.Dispose();
    }

    #endregion
}

/// <summary>
/// A stream the fake portal shares; properties that are <c>null</c> aren't sent.
/// </summary>
internal readonly record struct FakeStream(uint NodeId, string? Id = null, (int X, int Y)? Position = null, (int Width, int Height)? Size = null);
