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

    [TestMethod]
    public void DisposeOverrideRunsOnce()
    {
        CountingScreenCapture screenCapture = new();
        Assert.IsFalse(screenCapture.IsDisposed);

        screenCapture.Dispose();
        Assert.IsTrue(screenCapture.IsDisposed);
        screenCapture.Dispose();

        Assert.AreEqual(1, screenCapture.Disposals);
        Assert.IsTrue(screenCapture.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());
    }

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
