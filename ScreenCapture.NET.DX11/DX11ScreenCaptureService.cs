using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
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

    // So Dispose never releases the factory while it's used.
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

    /// <summary>
    /// Checks if Desktop Duplication can be used on this system.
    /// </summary>
    /// <remarks>
    /// Doesn't tell if every display can be duplicated: with two graphics cards (e.g. laptops) it can fail for a display of the other card.
    /// ScreenCapture.NET.WGC can capture those.
    /// </remarks>
    /// <returns><c>true</c> if it's supported; otherwise, <c>false</c>.</returns>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return CanCreateFactory();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Separate, so the DirectX types are only loaded on Windows.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CanCreateFactory()
    {
        bool created = DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Success;
        using (factory)
            return created;
    }

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        // Read right away, so the factory isn't used outside the lock.
        lock (_factoryLock)
        {
            ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

            return DXGIDevices.GetGraphicsCards(_factory);
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        // Read right away, so the factory isn't used outside the lock.
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
    /// A new capture gets the <see cref="Options"/> of the service, an existing one keeps its own.
    /// </remarks>
    public DX11ScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="DX11ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// There's one capture per <see cref="Display.Id"/>. An existing capture is only returned if it was created with equal options.
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
    /// Disposes all captures created by the service. Can be called more than once.
    /// </remarks>
    public void Dispose()
    {
        // The captures use the factory too.
        if (!_screenCaptures.DisposeAll()) return;

        lock (_factoryLock)
            try { _factory.Dispose(); } catch { /**/ }
    }

    #endregion
}