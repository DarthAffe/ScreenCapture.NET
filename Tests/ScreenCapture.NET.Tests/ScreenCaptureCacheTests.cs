namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks the capture cache the services share, with fake captures.
/// </summary>
[TestClass]
public sealed class ScreenCaptureCacheTests
{
    private static readonly TestOptions OPTIONS = new(1);
    private static readonly TestOptions OTHER_OPTIONS = new(2);

    private static Display CreateDisplay(string id, int index = 0)
        => new(id, index, $@"\\.\DISPLAY{index + 1}", string.Empty, 0, 0, 1920, 1080, Rotation.None, new GraphicsCard("card", 0, "card", 0, 0));

    [TestMethod]
    public void SameDisplayIdReturnsTheSameCapture()
    {
        TestCache cache = new();
        Display display = CreateDisplay("a");

        CacheTestScreenCapture capture = cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false);

        Assert.AreSame(capture, cache.GetOrCreate(display with { Index = 3, Width = 640 }, OPTIONS, requireEqualOptions: false));
        Assert.AreSame(capture, cache.GetOrCreate(display, OPTIONS, requireEqualOptions: true));
        Assert.HasCount(1, cache.Created);
        Assert.AreEqual(display, cache.CreatedFor[0]);
    }

    [TestMethod]
    public void EachDisplayGetsItsOwnCapture()
    {
        TestCache cache = new();

        CacheTestScreenCapture first = cache.GetOrCreate(CreateDisplay("a"), OPTIONS, requireEqualOptions: false);
        CacheTestScreenCapture second = cache.GetOrCreate(CreateDisplay("b"), OPTIONS, requireEqualOptions: false);

        Assert.AreNotSame(first, second);

        // Ids are compared ordinally.
        Assert.AreNotSame(first, cache.GetOrCreate(CreateDisplay("A"), OPTIONS, requireEqualOptions: false));
        Assert.HasCount(3, cache.Created);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        TestCache cache = new();
        Display display = CreateDisplay("a");

        CacheTestScreenCapture capture = cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false);
        capture.Dispose();

        // The new capture gets the requested options; without the check, they don't have to match the disposed one's.
        CacheTestScreenCapture replacement = cache.GetOrCreate(display, OTHER_OPTIONS, requireEqualOptions: false);

        Assert.AreNotSame(capture, replacement);
        Assert.AreSame(OTHER_OPTIONS, replacement.Options);
        Assert.IsFalse(IsDisposed(replacement));
        Assert.AreSame(replacement, cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false));
    }

    [TestMethod]
    public void DisposedCaptureIsReplacedAlsoWithOtherRequiredOptions()
    {
        TestCache cache = new();
        Display display = CreateDisplay("a");

        cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false).Dispose();

        CacheTestScreenCapture replacement = cache.GetOrCreate(display, OTHER_OPTIONS, requireEqualOptions: true);
        Assert.AreSame(OTHER_OPTIONS, replacement.Options);
    }

    [TestMethod]
    public void OptionsAreOnlyCheckedWhenRequired()
    {
        TestCache cache = new();
        Display display = CreateDisplay("a");
        CacheTestScreenCapture capture = cache.GetOrCreate(display, OPTIONS, requireEqualOptions: true);

        // Not required: the existing capture, whatever its options.
        Assert.AreSame(capture, cache.GetOrCreate(display, OTHER_OPTIONS, requireEqualOptions: false));

        // Required: equal (not the same) options return it, other options throw.
        Assert.AreSame(capture, cache.GetOrCreate(display, new TestOptions(1), requireEqualOptions: true));

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => cache.GetOrCreate(display, OTHER_OPTIONS, requireEqualOptions: true));
        StringAssert.Contains(exception.Message, "'a'");
        StringAssert.Contains(exception.Message, OPTIONS.ToString());

        Assert.HasCount(1, cache.Created);
        Assert.IsFalse(IsDisposed(capture));
    }

    [TestMethod]
    public void NullArgumentsThrow()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScreenCaptureCache<CacheTestScreenCapture, TestOptions>(null!, (_, o) => new CacheTestScreenCapture(o), c => c.Options));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScreenCaptureCache<CacheTestScreenCapture, TestOptions>(new object(), null!, c => c.Options));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScreenCaptureCache<CacheTestScreenCapture, TestOptions>(new object(), (_, o) => new CacheTestScreenCapture(o), null!));

        TestCache cache = new();
        Assert.ThrowsExactly<ArgumentNullException>(() => cache.GetOrCreate(CreateDisplay("a"), null!, requireEqualOptions: false));
        Assert.IsEmpty(cache.Created);
    }

    [TestMethod]
    public void FailedCreationIsPassedOnAndNotCached()
    {
        TestCache cache = new() { CreateException = new InvalidOperationException("Creation failed.") };
        Display display = CreateDisplay("a");

        Assert.ThrowsExactly<InvalidOperationException>(() => cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false));

        cache.CreateException = null;
        CacheTestScreenCapture capture = cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false);
        Assert.AreSame(capture, cache.GetOrCreate(display, OPTIONS, requireEqualOptions: false));
    }

    [TestMethod]
    public void DisposeAllDisposesEveryCaptureOnce()
    {
        TestCache cache = new();
        CacheTestScreenCapture first = cache.GetOrCreate(CreateDisplay("a"), OPTIONS, requireEqualOptions: false);
        CacheTestScreenCapture second = cache.GetOrCreate(CreateDisplay("b"), OPTIONS, requireEqualOptions: false);
        second.Dispose();

        Assert.IsFalse(cache.Cache.IsDisposed);
        Assert.IsTrue(cache.Cache.DisposeAll());
        Assert.IsTrue(cache.Cache.IsDisposed);

        Assert.IsTrue(IsDisposed(first));
        Assert.AreEqual(1, first.Disposals);
        Assert.AreEqual(1, second.Disposals);

        // Only the first call disposes.
        Assert.IsFalse(cache.Cache.DisposeAll());
        Assert.AreEqual(1, first.Disposals);
    }

    [TestMethod]
    public void RequestsAfterDisposeAllThrow()
    {
        TestCache cache = new();
        cache.Cache.DisposeAll();

        ObjectDisposedException exception = Assert.ThrowsExactly<ObjectDisposedException>(() => cache.GetOrCreate(CreateDisplay("a"), OPTIONS, requireEqualOptions: false));
        Assert.AreEqual(typeof(TestCache).FullName, exception.ObjectName);
        Assert.IsEmpty(cache.Created);
    }

    [TestMethod]
    public void ConcurrentRequestsGetOneCapturePerDisplay()
    {
        const int threads = 8;
        const int requests = 200;

        // A slow creation widens the window in which a second request could create another capture.
        TestCache cache = new() { CreateDelay = TimeSpan.FromMilliseconds(20) };
        Display[] displays = [CreateDisplay("a"), CreateDisplay("b")];

        using Barrier start = new(threads);
        List<CacheTestScreenCapture>[] results = [.. Enumerable.Range(0, threads).Select(_ => new List<CacheTestScreenCapture>())];
        Task[] tasks = [.. Enumerable.Range(0, threads).Select(thread => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < requests; i++)
                results[thread].Add(cache.GetOrCreate(displays[(thread + i) % displays.Length], OPTIONS, requireEqualOptions: false));
        }, TaskCreationOptions.LongRunning))];

        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "The requests didn't finish.");

        Assert.HasCount(2, cache.Created);
        foreach (CacheTestScreenCapture capture in results.SelectMany(r => r))
            Assert.Contains(capture, cache.Created);
    }

    [TestMethod]
    public void NoCaptureOutlivesDisposeAll()
    {
        const int threads = 4;

        for (int round = 0; round < 50; round++)
        {
            TestCache cache = new();
            using CancellationTokenSource stop = new();

            // Requests for ever new displays from several threads, so some wait for the lock (and create a capture) while DisposeAll runs.
            Task[] requests = [.. Enumerable.Range(0, threads).Select(thread => Task.Factory.StartNew(() =>
            {
                for (int i = 0; !stop.IsCancellationRequested; i++)
                {
                    try { cache.GetOrCreate(CreateDisplay($"display {thread}/{i}"), OPTIONS, requireEqualOptions: false); }
                    catch (ObjectDisposedException) { return; }
                }
            }, TaskCreationOptions.LongRunning))];

            SpinWait.SpinUntil(() => cache.CreatedCount > 20, TimeSpan.FromSeconds(5));
            cache.Cache.DisposeAll();
            stop.Cancel();
            Assert.IsTrue(Task.WaitAll(requests, TimeSpan.FromSeconds(5)), "The requests didn't stop.");

            List<CacheTestScreenCapture> notDisposed = [.. cache.Created.Where(c => !IsDisposed(c))];
            Assert.IsEmpty(notDisposed, $"Round {round}: {notDisposed.Count} of {cache.CreatedCount} captures weren't disposed.");
        }
    }

    [TestMethod]
    public void CapturesAreDisposedOutsideTheLock()
    {
        TestCache cache = new();
        CacheTestScreenCapture capture = cache.GetOrCreate(CreateDisplay("a"), OPTIONS, requireEqualOptions: false);

        // Disposing a real capture can wait for a running capture (e.g. the DX11 timeout).
        using ManualResetEventSlim disposing = new();
        using ManualResetEventSlim release = new();
        capture.OnDispose = () =>
        {
            disposing.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        Task disposeAll = Task.Factory.StartNew(() => cache.Cache.DisposeAll(), TaskCreationOptions.LongRunning);
        try
        {
            Assert.IsTrue(disposing.Wait(TimeSpan.FromSeconds(5)), "The capture wasn't disposed.");

            // Meanwhile, requests don't wait for the dispose: they fail right away.
            Task request = Task.Run(() => Assert.ThrowsExactly<ObjectDisposedException>(() => cache.GetOrCreate(CreateDisplay("b"), OPTIONS, requireEqualOptions: false)));
            Assert.IsTrue(request.Wait(TimeSpan.FromSeconds(5)), "The request waited for the dispose.");
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(disposeAll.Wait(TimeSpan.FromSeconds(5)));
    }

    private static bool IsDisposed(CacheTestScreenCapture capture) => capture.IsDisposed;

    private sealed record TestOptions(int Value);

    private sealed class CacheTestScreenCapture(TestOptions options) : TestScreenCapture
    {
        private bool _isDisposed;

        public TestOptions Options { get; } = options;

        public int Disposals { get; private set; }

        public Action? OnDispose { get; set; }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Disposals++;
            OnDispose?.Invoke();

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A cache owned by this object, recording the captures it creates.
    /// </summary>
    private sealed class TestCache
    {
        private readonly Lock _lock = new();
        private readonly List<CacheTestScreenCapture> _created = [];
        private readonly List<Display> _createdFor = [];

        public ScreenCaptureCache<CacheTestScreenCapture, TestOptions> Cache { get; }

        public TimeSpan CreateDelay { get; init; }

        public Exception? CreateException { get; set; }

        public List<CacheTestScreenCapture> Created { get { lock (_lock) return [.. _created]; } }

        public List<Display> CreatedFor { get { lock (_lock) return [.. _createdFor]; } }

        public int CreatedCount { get { lock (_lock) return _created.Count; } }

        public TestCache()
        {
            Cache = new ScreenCaptureCache<CacheTestScreenCapture, TestOptions>(this, Create, capture => capture.Options);
        }

        public CacheTestScreenCapture GetOrCreate(Display display, TestOptions options, bool requireEqualOptions)
            => Cache.GetOrCreate(display, options, requireEqualOptions);

        private CacheTestScreenCapture Create(Display display, TestOptions options)
        {
            if (CreateException != null) throw CreateException;
            if (CreateDelay > TimeSpan.Zero) Thread.Sleep(CreateDelay);

            CacheTestScreenCapture capture = new(options);
            lock (_lock)
            {
                _created.Add(capture);
                _createdFor.Add(display);
            }

            return capture;
        }
    }
}
