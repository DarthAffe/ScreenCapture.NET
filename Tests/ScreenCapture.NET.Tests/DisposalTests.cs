extern alias WGC;
extern alias Wayland;
using System.Reflection;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class DisposalTests
{
    private static readonly Assembly[] LIBRARY_ASSEMBLIES =
    [
        typeof(AbstractScreenCapture<>).Assembly,
        typeof(DX11ScreenCaptureService).Assembly,
        typeof(DX9ScreenCaptureService).Assembly,
        typeof(X11ScreenCaptureService).Assembly,
        typeof(WGC::ScreenCapture.NET.WGCScreenCaptureService).Assembly,
        typeof(Wayland::ScreenCapture.NET.WaylandScreenCaptureService).Assembly
    ];

    /// <summary>
    /// Finalizers run on the finalizer thread, where releasing a <see cref="Lock"/> or disposing other managed objects crashes or races.
    /// The library relies on <see cref="IDisposable.Dispose"/> only (native handles get a SafeHandle).
    /// </summary>
    [TestMethod]
    public void LibraryTypesHaveNoFinalizers()
    {
        List<string> typesWithFinalizer = [.. LIBRARY_ASSEMBLIES.SelectMany(assembly => assembly.GetTypes())
                                                                .Where(type => type.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null)
                                                                .Select(type => type.FullName ?? type.Name)];

        Assert.IsEmpty(typesWithFinalizer, $"Types with a finalizer: {string.Join(", ", typesWithFinalizer)}");
    }

    [TestMethod]
    public void DisposeOverrideRunsOnce()
    {
        CountingScreenCapture screenCapture = new();
        Assert.IsFalse(screenCapture.IsDisposed);

        screenCapture.Dispose();
        Assert.IsTrue(screenCapture.IsDisposed);
        screenCapture.Dispose();

        Assert.AreEqual(1, screenCapture.Disposals);
        Assert.IsTrue(screenCapture.DisposedBeforeRelease, "IsDisposed wasn't set before the override ran.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());
    }

    [TestMethod]
    public void ConcurrentDisposeRunsTheOverrideOnce()
    {
        for (int round = 0; round < 100; round++)
        {
            CountingScreenCapture screenCapture = new();
            using Barrier barrier = new(4);

            Task[] disposals = [.. Enumerable.Range(0, barrier.ParticipantCount).Select(_ => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                screenCapture.Dispose();
            }, TaskCreationOptions.LongRunning))];

            Assert.IsTrue(Task.WaitAll(disposals, TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1, screenCapture.Disposals, $"Round {round}.");
        }
    }

    /// <summary>
    /// A service releases the objects its captures share once their Dispose returned: a second Dispose must not return while the first still releases the backend.
    /// </summary>
    [TestMethod]
    public void SecondDisposeWaitsForTheFirst()
    {
        using ManualResetEventSlim releasing = new();
        using ManualResetEventSlim release = new();
        BlockingScreenCapture screenCapture = new(releasing, release);

        Task first = Task.Factory.StartNew(screenCapture.Dispose, TaskCreationOptions.LongRunning);
        Assert.IsTrue(releasing.Wait(TimeSpan.FromSeconds(10)), "The first Dispose didn't release the backend.");

        bool releasedBeforeReturn = false;
        Task second = Task.Factory.StartNew(() =>
        {
            screenCapture.Dispose();
            releasedBeforeReturn = release.IsSet;
        }, TaskCreationOptions.LongRunning);

        // Gives a second Dispose that doesn't wait the time to return.
        Assert.IsFalse(second.Wait(TimeSpan.FromMilliseconds(200)), "The second Dispose returned while the first one still released the backend.");

        release.Set();
        Assert.IsTrue(Task.WaitAll([first, second], TimeSpan.FromSeconds(10)));
        Assert.IsTrue(releasedBeforeReturn);
    }

    private sealed class BlockingScreenCapture(ManualResetEventSlim releasing, ManualResetEventSlim release) : TestScreenCapture
    {
        protected override void Dispose(bool disposing)
        {
            releasing.Set();
            release.Wait();

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Follows the pattern documented on <see cref="AbstractScreenCapture{TColor}.Dispose(bool)"/>: no own flag, the base runs it once.
    /// </summary>
    private sealed class CountingScreenCapture : TestScreenCapture
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public bool DisposedBeforeRelease { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposedBeforeRelease = IsDisposed;
            Interlocked.Increment(ref _disposals);

            base.Dispose(disposing);
        }
    }
}
