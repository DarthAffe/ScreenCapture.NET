using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="WGCScreenCapture"/>.
/// </summary>
/// <remarks>
/// The graphics cards and displays are the same as the ones of the DX11 backend (same <see cref="Display.Id"/>s).
/// Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, WGCCaptureOptions)"/>.
/// Besides displays it can capture windows (<see cref="GetWindowCapture(nint)"/>).
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class WGCScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    // Enumerations use the factory under this lock and Dispose releases it under it, so it's never released while it's used.
    private readonly Lock _factoryLock = new();
    private readonly IDXGIFactory1 _factory;
    private readonly ScreenCaptureCache<WGCScreenCapture, WGCCaptureOptions> _screenCaptures;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public WGCCaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WGCScreenCaptureService"/> class.
    /// </summary>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="WGCCaptureOptions.Default"/>.</param>
    /// <exception cref="PlatformNotSupportedException">Windows.Graphics.Capture isn't supported on this system (see <see cref="IsSupported"/>).</exception>
    public WGCScreenCaptureService(WGCCaptureOptions? options = null)
    {
        if (!IsSupported()) throw new PlatformNotSupportedException("Windows.Graphics.Capture isn't supported on this system.");

        Options = options ?? WGCCaptureOptions.Default;

        DXGI.CreateDXGIFactory1(out _factory!).CheckError();
        _screenCaptures = new ScreenCaptureCache<WGCScreenCapture, WGCCaptureOptions>(this, (display, captureOptions) => new WGCScreenCapture(_factory, display, captureOptions), capture => capture.Options);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Checks if Windows.Graphics.Capture can be used on this system.
    /// </summary>
    /// <remarks>
    /// It needs Windows 10 1903 or later and a desktop composition (e.g. not on Windows Server Core).
    /// </remarks>
    /// <returns><c>true</c> if it's supported; otherwise, <c>false</c>.</returns>
    public static bool IsSupported() => WGCInterop.IsSupported();

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        // Read right away (not while enumerating), so the factory isn't used outside the lock.
        lock (_factoryLock)
        {
            ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

            return DXGIDevices.GetGraphicsCards(_factory);
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        // Read right away (not while enumerating), so the factory isn't used outside the lock.
        lock (_factoryLock)
        {
            ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

            return DXGIDevices.GetDisplays(_factory, graphicsCard);
        }
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </remarks>
    public WGCScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="WGCScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to capture.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="WGCScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public WGCScreenCapture GetScreenCapture(Display display, WGCCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return _screenCaptures.GetOrCreate(display, options, requireEqualOptions: true);
    }

    /// <summary>
    /// Gets the capture of a window.
    /// </summary>
    /// <remarks>
    /// The window is captured as it's shown, also while other windows cover it. Its <see cref="IScreenCapture.Display"/> describes the window (see <see cref="WGCScreenCapture"/>);
    /// its <see cref="Display.Id"/> is only valid while the window exists. A resize restarts the capture with the new size (<see cref="IScreenCapture.DisplayChanged"/>).
    /// <para>
    /// Windows.Graphics.Capture refuses some windows (e.g. tool windows and ones that can't be activated, like the taskbar): their capture is <see cref="CaptureResult.Unavailable"/>
    /// and <see cref="IScreenCapture.LastError"/> tells why.
    /// </para>
    /// <para>
    /// The service creates one capture per window and returns it again for the same handle. A capture that was disposed is replaced by a new one.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </para>
    /// </remarks>
    /// <param name="window">The handle (<c>HWND</c>) of a top-level window (e.g. <c>Process.MainWindowHandle</c>).</param>
    /// <returns>The <see cref="WGCScreenCapture"/> of the window.</returns>
    /// <exception cref="ArgumentException"><paramref name="window"/> isn't the handle of an existing top-level window.</exception>
    public WGCScreenCapture GetWindowCapture(nint window) => GetWindowCapture(window, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="WGCScreenCapture"/> with the given options for a window.
    /// </summary>
    /// <remarks>
    /// See <see cref="GetWindowCapture(nint)"/>. An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="window">The handle (<c>HWND</c>) of a top-level window (e.g. <c>Process.MainWindowHandle</c>).</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="WGCScreenCapture"/> of the window.</returns>
    /// <exception cref="ArgumentException"><paramref name="window"/> isn't the handle of an existing top-level window.</exception>
    /// <exception cref="InvalidOperationException">The capture of the window already exists with other options.</exception>
    public WGCScreenCapture GetWindowCapture(nint window, WGCCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return GetWindowCapture(window, options, requireEqualOptions: true);
    }

    private WGCScreenCapture GetWindowCapture(nint window, WGCCaptureOptions options, bool requireEqualOptions)
    {
        if (!WindowInterop.IsTopLevelWindow(window))
            throw new ArgumentException($"0x{(long)window:X} isn't the handle of an existing top-level window.", nameof(window));

        // Cached with the displays: the capture reads the window from the id.
        return _screenCaptures.GetOrCreate(WindowInterop.CreateUnknownDisplay(window), options, requireEqualOptions);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disposes all captures created by the service. It's thread-safe and can be called more than once.
    /// </remarks>
    public void Dispose()
    {
        // The captures use the factory too (when they restart): they're disposed first.
        if (!_screenCaptures.DisposeAll()) return;

        lock (_factoryLock)
            try { _factory.Dispose(); } catch { /**/ }
    }

    #endregion
}
