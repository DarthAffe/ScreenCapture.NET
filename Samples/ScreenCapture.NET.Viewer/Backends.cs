using System.Diagnostics;
using System.Runtime.Versioning;

namespace ScreenCapture.NET.Viewer;

/// <summary>
/// The capture backends, in the order the README recommends them.
/// </summary>
internal static class Backends
{
    #region Constants

    public const string WGC = "WGC";
    public const string DX11 = "DX11";
    public const string DX9 = "DX9";
    public const string X11 = "X11";
    public const string Wayland = "Wayland";

    // The Wayland restore token is kept here, so only the first start shows the dialog of the desktop.
    private static readonly string RestoreTokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCapture.NET.Viewer", "wayland-restore-token");

    #endregion

    #region Methods

    /// <summary>
    /// Gets the backends that work on this system. The Wayland check asks the portal, so this can take a moment.
    /// </summary>
    public static List<string> GetSupported()
    {
        List<string> supported = [];
        if (WGCScreenCaptureService.IsSupported()) supported.Add(WGC);
        if (DX11ScreenCaptureService.IsSupported()) supported.Add(DX11);
        if (DX9ScreenCaptureService.IsSupported()) supported.Add(DX9);
        if (X11ScreenCaptureService.IsSupported()) supported.Add(X11);
        if (WaylandScreenCaptureService.IsSupported()) supported.Add(Wayland);
        return supported;
    }

    /// <summary>
    /// Creates the service of a backend. Only the Wayland session options are used here, the others go to each capture.
    /// </summary>
    public static IScreenCaptureService Create(string backend, ViewerOptions options)
    {
        // Every branch checks IsSupported again: it's the platform guard for the constructor.
        if ((backend == WGC) && WGCScreenCaptureService.IsSupported()) return new WGCScreenCaptureService();
        if ((backend == DX11) && DX11ScreenCaptureService.IsSupported()) return new DX11ScreenCaptureService();
        if ((backend == DX9) && DX9ScreenCaptureService.IsSupported()) return new DX9ScreenCaptureService();
        if ((backend == X11) && X11ScreenCaptureService.IsSupported()) return new X11ScreenCaptureService();

        if ((backend == Wayland) && WaylandScreenCaptureService.IsSupported())
        {
            WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions
            {
                DrawCursor = options.DrawCursor,
                PersistMode = (WaylandPersistMode)options.PersistMode,
                RestoreToken = LoadRestoreToken()
            });

            // The desktop may hand out a new token with every session (GNOME does): always keep the latest one.
            service.RestoreTokenChanged += OnRestoreTokenChanged;
            return service;
        }

        throw new PlatformNotSupportedException($"{backend} isn't supported on this system.");
    }

    /// <summary>
    /// Gets what the service can capture: its displays, and with WGC the main windows of all processes.
    /// </summary>
    public static List<CaptureSource> GetSources(IScreenCaptureService service)
    {
        List<CaptureSource> sources = [.. service.GetGraphicsCards().SelectMany(service.GetDisplays).Select(display => new CaptureSource(Describe(display), display, 0))];

        if (OperatingSystem.IsWindows() && (service is WGCScreenCaptureService))
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if ((process.MainWindowHandle != 0) && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        sources.Add(new CaptureSource($"Window: {process.MainWindowTitle} ({process.ProcessName})", null, process.MainWindowHandle));
                }
            }
        }

        return sources;
    }

    /// <summary>
    /// Creates the capture of a source with the options of its backend. Created anew on every start, so changed options apply.
    /// </summary>
    public static IScreenCapture GetCapture(IScreenCaptureService service, CaptureSource source, ViewerOptions options)
    {
        if (OperatingSystem.IsWindows())
        {
            if (service is WGCScreenCaptureService wgc)
            {
                WGCCaptureOptions wgcOptions = new() { Timeout = options.Timeout, CaptureHdr = options.CaptureHdr, DrawCursor = options.DrawCursor, ShowBorder = options.ShowBorder };
                return source.Display is { } wgcDisplay ? wgc.GetScreenCapture(wgcDisplay, wgcOptions) : wgc.GetWindowCapture(source.Window, wgcOptions);
            }

            if ((service is DX11ScreenCaptureService dx11) && (source.Display is { } dx11Display))
                return dx11.GetScreenCapture(dx11Display, new DX11CaptureOptions { Timeout = options.Timeout, CaptureHdr = options.CaptureHdr, DrawCursor = options.DrawCursor });
        }

        if (OperatingSystem.IsLinux())
        {
            if ((service is X11ScreenCaptureService x11) && (source.Display is { } x11Display))
                return x11.GetScreenCapture(x11Display, new X11CaptureOptions { UseSharedMemory = options.UseSharedMemory });

            if ((service is WaylandScreenCaptureService wayland) && (source.Display is { } waylandDisplay))
                return wayland.GetScreenCapture(waylandDisplay, new WaylandCaptureOptions { Timeout = options.Timeout });
        }

        // DX9 has no options.
        if (source.Display is { } display)
            return service.GetScreenCapture(display);

        throw new InvalidOperationException("Only WGC can capture windows.");
    }

    /// <summary>
    /// Deletes the stored Wayland restore token: the next session shows the dialog of the desktop again.
    /// </summary>
    public static void ForgetRestoreToken()
    {
        try
        {
            File.Delete(RestoreTokenPath);
        }
        catch (IOException) { }
    }

    private static string? LoadRestoreToken()
    {
        try
        {
            return File.Exists(RestoreTokenPath) ? File.ReadAllText(RestoreTokenPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // A method instead of a lambda: the platform check of the caller doesn't reach into a lambda.
    [SupportedOSPlatform("linux")]
    private static void OnRestoreTokenChanged(object? sender, WaylandRestoreTokenChangedEventArgs e) => SaveRestoreToken(e.RestoreToken);

    private static void SaveRestoreToken(string restoreToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestoreTokenPath)!);
            File.WriteAllText(RestoreTokenPath, restoreToken);
        }
        catch (IOException) { }
    }

    public static string Describe(Display display)
    {
        string name = string.IsNullOrEmpty(display.FriendlyName) ? display.DeviceName : display.FriendlyName;
        return $"{name} ({display.Width}x{display.Height} at {display.X},{display.Y}, {display.Rotation})";
    }

    #endregion
}

/// <summary>
/// Something to capture: a display, or a window (WGC).
/// </summary>
internal sealed record CaptureSource(string Name, Display? Display, nint Window)
{
    public override string ToString() => Name;
}

/// <summary>
/// The options of the viewer; each backend uses the ones it has.
/// </summary>
/// <param name="Timeout">WGC, DX11 and Wayland.</param>
/// <param name="CaptureHdr">WGC and DX11.</param>
/// <param name="DrawCursor">WGC, DX11 and Wayland (a session option there).</param>
/// <param name="ShowBorder">WGC.</param>
/// <param name="UseSharedMemory">X11.</param>
/// <param name="PersistMode">Wayland (session option): the value of <c>WaylandPersistMode</c>.</param>
internal sealed record ViewerOptions(TimeSpan Timeout, bool CaptureHdr, bool DrawCursor, bool ShowBorder, bool UseSharedMemory, int PersistMode);
