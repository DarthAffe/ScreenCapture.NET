extern alias Wayland;
using System.Runtime.Versioning;
using HPPH;
using Tmds.DBus.Protocol;
using Wayland::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Checks the screen cast session of the Wayland service against a <see cref="FakeScreenCastPortal"/> on a <see cref="PrivateSessionBus"/>: no dialog, no compositor needed.
/// Inconclusive in a process that also runs the Interactive tests (see <see cref="PrivateSessionBus"/>).
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WaylandPortalTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void DisplaysAreTheSharedStreams()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.Streams.Clear();
        portal.Streams.AddRange([new FakeStream(40, "left", (-1280, 0), (1280, 1024)), new FakeStream(41, "main", (0, 0), (2560, 1440))]);

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        List<Display> displays = [.. GetDisplays(service, graphicsCard)];

        CollectionAssert.AreEqual(new List<Display>
                                  {
                                      new("stream:left", 0, "Stream 0", string.Empty, -1280, 0, 1280, 1024, ImageRotation.None, graphicsCard),
                                      new("stream:main", 1, "Stream 1", string.Empty, 0, 0, 2560, 1440, ImageRotation.None, graphicsCard)
                                  }, displays);
    }

    /// <summary>
    /// Like KDE: streams without id and position.
    /// </summary>
    [TestMethod]
    public void StreamsWithoutIdAreNamedByIndex()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.Streams.Clear();
        portal.Streams.AddRange([new FakeStream(40, Size: (1920, 1080)), new FakeStream(41, Size: (1280, 720)), new FakeStream(42)]);

        using WaylandScreenCaptureService service = new();
        List<Display> displays = [.. GetDisplays(service, service.GetGraphicsCards().Single())];

        CollectionAssert.AreEqual(new[] { "stream:0", "stream:1", "stream:2" }, displays.Select(display => display.Id).ToArray());
        CollectionAssert.AreEqual(new[] { (0, 0, 1920, 1080), (0, 0, 1280, 720), (0, 0, 0, 0) }, displays.Select(display => (display.X, display.Y, display.Width, display.Height)).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionHandleIsAStringOrAnObjectPath(bool asObjectPath)
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.SessionHandleAsObjectPath = asObjectPath;

        using WaylandScreenCaptureService service = new();
        Assert.ContainsSingle(GetDisplays(service, service.GetGraphicsCards().Single()));
        CollectionAssert.AreEqual(new[] { "CreateSession", "SelectSources", "Start" }, portal.Calls.ToArray());
    }

    [TestMethod]
    public void SessionAsksForAnyNumberOfMonitors()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new();
        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Dictionary<string, VariantValue>? options = portal.SelectSourcesOptions;
        Assert.IsNotNull(options);
        Assert.AreEqual(1u, options["types"].GetUInt32(), "monitors");
        Assert.IsTrue(options["multiple"].GetBool());
    }

    [TestMethod]
    [DataRow(false, 7u, 1u)]
    [DataRow(true, 7u, 2u)]
    [DataRow(true, 3u, 2u)]
    public void CursorModeIsTheOneOfTheOptions(bool drawCursor, uint availableCursorModes, uint cursorMode)
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.AvailableCursorModes = availableCursorModes;

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { DrawCursor = drawCursor });
        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Assert.AreEqual(cursorMode, portal.SelectSourcesOptions?["cursor_mode"].GetUInt32());
    }

    /// <summary>
    /// A mode the portal doesn't offer would end the session: none is asked for (the portal hides the pointer then).
    /// </summary>
    [TestMethod]
    [DataRow(true, 5u)]
    [DataRow(false, 2u)]
    [DataRow(true, null)]
    public void CursorModeIsOnlyAskedForIfOffered(bool drawCursor, uint? availableCursorModes)
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.AvailableCursorModes = availableCursorModes;

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { DrawCursor = drawCursor });
        Assert.ContainsSingle(GetDisplays(service, service.GetGraphicsCards().Single()));

        Assert.IsFalse(portal.SelectSourcesOptions?.ContainsKey("cursor_mode") ?? true);
    }

    [TestMethod]
    public void SessionIsRememberedByDefault()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new();
        List<string> changes = [];
        service.RestoreTokenChanged += (_, args) => changes.Add(args.RestoreToken);
        Assert.IsNull(service.RestoreToken);

        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Assert.AreEqual((uint)WaylandPersistMode.Persistent, portal.SelectSourcesOptions?["persist_mode"].GetUInt32());
        Assert.IsFalse(portal.SelectSourcesOptions?.ContainsKey("restore_token") ?? true);
        Assert.AreEqual("token-1", service.RestoreToken);
        CollectionAssert.AreEqual(new[] { "token-1" }, changes);
    }

    [TestMethod]
    [DataRow(WaylandPersistMode.WhileRunning)]
    [DataRow(WaylandPersistMode.Persistent)]
    public void PersistModeIsTheOneOfTheOptions(WaylandPersistMode persistMode)
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { PersistMode = persistMode });
        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Assert.AreEqual((uint)persistMode, portal.SelectSourcesOptions?["persist_mode"].GetUInt32());
    }

    [TestMethod]
    public void NothingIsRememberedWithoutPersistMode()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { PersistMode = WaylandPersistMode.None });
        bool changed = false;
        service.RestoreTokenChanged += (_, _) => changed = true;

        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Assert.IsFalse(portal.SelectSourcesOptions?.ContainsKey("persist_mode") ?? true);
        Assert.IsNull(service.RestoreToken);
        Assert.IsFalse(changed);
    }

    /// <summary>
    /// Like a later process with the token the first one stored: no dialog.
    /// </summary>
    [TestMethod]
    public void TokenOfTheOptionsRestoresTheSession()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        string token;
        using (WaylandScreenCaptureService first = new())
        {
            _ = GetDisplays(first, first.GetGraphicsCards().Single());
            token = first.RestoreToken!;
        }

        using WaylandScreenCaptureService second = new(sessionOptions: new WaylandSessionOptions { RestoreToken = token });
        Assert.AreEqual(token, second.RestoreToken);

        _ = GetDisplays(second, second.GetGraphicsCards().Single());

        Assert.AreEqual(token, portal.SelectSourcesOptions?["restore_token"].GetString());
        Assert.AreEqual(1, portal.Dialogs, "The second session showed the dialog.");
        Assert.AreEqual("token-2", second.RestoreToken, "Not the new token (rotated).");
    }

    /// <summary>
    /// A new session after the last one ended (in the same process) restores with the latest token on its own.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NextSessionRestoresWithTheLatestToken(bool rotatesTokens)
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.RotatesRestoreTokens = rotatesTokens;

        using WaylandScreenCaptureService service = new();
        List<string> changes = [];
        service.RestoreTokenChanged += (_, args) => changes.Add(args.RestoreToken);
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        _ = GetDisplays(service, graphicsCard);

        portal.CloseSessions();
        Assert.IsTrue(SpinWait.SpinUntil(() => GetDisplays(service, graphicsCard).Any() && (portal.CountCalls("CreateSession") == 2), WaitTimeout), "No new session was started.");

        Assert.AreEqual("token-1", portal.SelectSourcesOptions?["restore_token"].GetString());
        Assert.AreEqual(1, portal.Dialogs, "The second session showed the dialog.");

        // GNOME hands out a new token (the event tells), KDE keeps it (no event).
        string[] expected = rotatesTokens ? ["token-1", "token-2"] : ["token-1"];
        CollectionAssert.AreEqual(expected, changes);
        Assert.AreEqual(expected[^1], service.RestoreToken);
    }

    [TestMethod]
    public void InvalidTokenShowsTheDialog()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { RestoreToken = "revoked" });
        _ = GetDisplays(service, service.GetGraphicsCards().Single());

        Assert.AreEqual(1, portal.Dialogs);
        Assert.AreEqual("token-1", service.RestoreToken);
    }

    /// <summary>
    /// Before version 4 the portal has no restore tokens: they aren't sent.
    /// </summary>
    [TestMethod]
    public void OldPortalGetsNoRestoreOptions()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.Version = 3;

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { RestoreToken = "saved" });
        Assert.ContainsSingle(GetDisplays(service, service.GetGraphicsCards().Single()));

        Assert.IsFalse(portal.SelectSourcesOptions?.ContainsKey("persist_mode") ?? true);
        Assert.IsFalse(portal.SelectSourcesOptions?.ContainsKey("restore_token") ?? true);
        Assert.AreEqual("saved", service.RestoreToken, "The token of the options stays.");
    }

    [TestMethod]
    public void ResponseBeforeTheReplyIsntMissed()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.RespondBeforeReply = true;

        using WaylandScreenCaptureService service = new();
        Assert.ContainsSingle(GetDisplays(service, service.GetGraphicsCards().Single()));
    }

    [TestMethod]
    public void LaterCallsReturnTheDisplaysOfTheSession()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        List<Display> first = [.. GetDisplays(service, graphicsCard)];
        List<Display> second = [.. GetDisplays(service, graphicsCard)];

        CollectionAssert.AreEqual(first, second);
        Assert.AreEqual(1, portal.CountCalls("Start"));
    }

    [TestMethod]
    public void OnlyTheDisplaysOfThePortalStartASession()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        Assert.AreEqual("xdg-desktop-portal", graphicsCard.Id);

        Assert.IsEmpty(GetDisplays(service, graphicsCard with { Id = "other" }));
        Assert.IsEmpty(portal.Calls);
    }

    [TestMethod]
    public void CancelledDialogThrowsAndTheNextCallAsksAgain()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.StartResponse = 1;

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => GetDisplays(service, graphicsCard));
        Assert.Contains("cancelled", exception.Message);

        portal.StartResponse = 0;
        Assert.ContainsSingle(GetDisplays(service, graphicsCard));
        Assert.AreEqual(2, portal.CountCalls("CreateSession"));
    }

    [TestMethod]
    public void FailedStartThrows()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.StartResponse = 2;

        using WaylandScreenCaptureService service = new();
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => GetDisplays(service, service.GetGraphicsCards().Single()));
        Assert.Contains("response 2", exception.Message);
    }

    [TestMethod]
    public void MissingPortalThrows()
    {
        _ = PrivateSessionBus.Address;

        using WaylandScreenCaptureService service = new();
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => GetDisplays(service, service.GetGraphicsCards().Single()));
        Assert.Contains("isn't available", exception.Message);
    }

    [TestMethod]
    public void ClosedSessionIsReplacedByTheNextCall()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        _ = GetDisplays(service, graphicsCard);

        portal.CloseSessions();

        // The signal arrives asynchronously; until then the call returns the displays of the old session.
        Assert.IsTrue(SpinWait.SpinUntil(() => GetDisplays(service, graphicsCard).Any() && (portal.CountCalls("CreateSession") == 2), WaitTimeout), "No new session was started.");
        Assert.AreEqual(2, portal.CountCalls("Start"));
    }

    [TestMethod]
    public void DisposeEndsTheSession()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();

        WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        _ = GetDisplays(service, graphicsCard);

        service.Dispose();
        Assert.AreEqual("Close", portal.Calls[^1]);

        Assert.ThrowsExactly<ObjectDisposedException>(() => GetDisplays(service, graphicsCard));
        Assert.ThrowsExactly<ObjectDisposedException>(service.GetGraphicsCards);
        service.Dispose();
    }

    [TestMethod]
    public void DisposeEndsAWaitForTheUser()
    {
        using FakeScreenCastPortal portal = FakeScreenCastPortal.Start();
        portal.StartAnswered = new TaskCompletionSource().Task; // the user never answers

        WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        Task<List<Display>> getDisplays = Task.Run(() => service.GetDisplays(graphicsCard).ToList());
        Assert.IsTrue(SpinWait.SpinUntil(() => portal.CountCalls("Start") == 1, WaitTimeout), "The dialog wasn't shown.");
        Assert.IsFalse(getDisplays.IsCompleted);

        service.Dispose();

        Assert.IsTrue(((IAsyncResult)getDisplays).AsyncWaitHandle.WaitOne(WaitTimeout), "GetDisplays still waits.");
        Assert.IsInstanceOfType<ObjectDisposedException>(getDisplays.Exception?.InnerException);
        Assert.Contains("Close", portal.Calls);
    }

    // A wrong request path or a missed response would wait forever.
    private static List<Display> GetDisplays(WaylandScreenCaptureService service, GraphicsCard graphicsCard)
    {
        Task<List<Display>> getDisplays = Task.Run(() => service.GetDisplays(graphicsCard).ToList());
        Assert.IsTrue(((IAsyncResult)getDisplays).AsyncWaitHandle.WaitOne(WaitTimeout), "GetDisplays doesn't return.");

        return getDisplays.GetAwaiter().GetResult();
    }
}
