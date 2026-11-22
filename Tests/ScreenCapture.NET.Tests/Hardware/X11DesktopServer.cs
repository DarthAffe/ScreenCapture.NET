using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// The X server of the session ($DISPLAY) for the X11 tests that capture the desktop.
/// They're inconclusive without one and on XWayland, which doesn't show the Wayland desktop.
/// Tests on a private Xvfb server (see <see cref="XvfbServer"/>) run without it.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class X11DesktopServer
{
    #region Properties & Fields

    // Why the desktop can't be captured over X11; null if it can. Checked once: $DISPLAY doesn't change while the tests run.
    private static readonly Lazy<string?> UnavailableReason = new(GetUnavailableReason);

    #endregion

    #region Methods

    /// <summary>
    /// Makes the test inconclusive if the session has no X server that shows the desktop.
    /// </summary>
    public static void Require()
    {
        if (UnavailableReason.Value is { } reason)
            Assert.Inconclusive(reason);
    }

    /// <summary>
    /// Creates a service; one for the X server of the session (<paramref name="displayName"/> <c>null</c>) only if it shows the desktop (see <see cref="Require"/>).
    /// </summary>
    public static X11ScreenCaptureService CreateService(X11CaptureOptions? options = null, string? displayName = null)
    {
        if (displayName == null) Require();
        return new X11ScreenCaptureService(options, displayName);
    }

    /// <summary>
    /// Opens a connection to the X server of the session, if it shows the desktop (see <see cref="Require"/>).
    /// </summary>
    public static X11DisplayHandle Open()
    {
        Require();
        return X11DisplayHandle.Open(null);
    }

    private static string? GetUnavailableReason()
    {
        // Without an X server the service throws (see X11NoServerTests).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return "No X server available ($DISPLAY is not set).";

        using X11DisplayHandle display = X11DisplayHandle.Open(null);
        if (display.IsInvalid) return null; // the tests report it

        return XQueryExtension(display, "XWAYLAND", out _, out _, out _)
                   ? "The X server of the session is XWayland, which doesn't show the Wayland desktop (use the Wayland backend there)."
                   : null;
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XQueryExtension(X11DisplayHandle display, string name, out int majorOpcode, out int firstEvent, out int firstError);

    #endregion
}
