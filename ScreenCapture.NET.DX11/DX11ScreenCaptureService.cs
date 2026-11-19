using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, DX11CaptureOptions)"/>.
/// </remarks>
public sealed class DX11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    // Enumerations use the factory under this lock and Dispose releases it under it, so it's never released while it's used.
    private readonly Lock _factoryLock = new();
    private readonly IDXGIFactory1 _factory;
    private readonly ScreenCaptureCache<DX11ScreenCapture, DX11CaptureOptions> _screenCaptures;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public DX11CaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX11ScreenCaptureService"/> class.
    /// </summary>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="DX11CaptureOptions.Default"/>.</param>
    public DX11ScreenCaptureService(DX11CaptureOptions? options = null)
    {
        Options = options ?? DX11CaptureOptions.Default;

        DXGI.CreateDXGIFactory1(out _factory!).CheckError();
        _screenCaptures = new ScreenCaptureCache<DX11ScreenCapture, DX11CaptureOptions>(this, (display, captureOptions) => new DX11ScreenCapture(_factory, display, captureOptions), capture => capture.Options);
    }

    #endregion

    #region Methods

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
    public DX11ScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="DX11ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="DX11ScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public DX11ScreenCapture GetScreenCapture(Display display, DX11CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return _screenCaptures.GetOrCreate(display, options, requireEqualOptions: true);
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