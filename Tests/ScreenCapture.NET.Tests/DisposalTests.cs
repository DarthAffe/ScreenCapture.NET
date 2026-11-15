using System.Reflection;
using System.Runtime.CompilerServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class DisposalTests
{
    private static readonly Assembly[] LIBRARY_ASSEMBLIES =
    [
        typeof(AbstractScreenCapture<>).Assembly,
        typeof(DX11ScreenCaptureService).Assembly,
        typeof(DX9ScreenCaptureService).Assembly,
        typeof(X11ScreenCaptureService).Assembly
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

    /// <summary>
    /// A zone lock that's never disposed must not crash the process when it's collected
    /// (the old finalizer released the lock on the finalizer thread, which throws <see cref="SynchronizationLockException"/>).
    /// </summary>
    [TestMethod]
    public void AbandonedZoneLockIsCollectedWithoutCrash()
    {
        using TestScreenCapture screenCapture = new();
        WeakReference abandonedLock = LockWithoutDispose(screenCapture.RegisterCaptureZone(0, 0, 16, 8));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsFalse(abandonedLock.IsAlive);
    }

    [TestMethod]
    public void DisposeOverrideRunsOnce()
    {
        CountingScreenCapture screenCapture = new();

        screenCapture.Dispose();
        screenCapture.Dispose();

        Assert.AreEqual(1, screenCapture.Disposals);
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LockWithoutDispose(CaptureZone<ColorARGB> zone) => new(zone.Lock());

    /// <summary>
    /// Follows the pattern documented on <see cref="AbstractScreenCapture{TColor}.Dispose(bool)"/>.
    /// </summary>
    private sealed class CountingScreenCapture : TestScreenCapture
    {
        private bool _isDisposed;

        public int Disposals { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Disposals++;

            base.Dispose(disposing);
        }
    }
}
