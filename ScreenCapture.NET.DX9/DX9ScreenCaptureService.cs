using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX9ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, DX9CaptureOptions)"/>.
/// </remarks>
public sealed class DX9ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    // So Dispose never releases the D3D9 instance while it's used.
    private readonly Lock _direct3D9Lock = new();
    private readonly IDirect3D9Ex _direct3D9;
    private readonly ScreenCaptureCache<DX9ScreenCapture, DX9CaptureOptions> _screenCaptures;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public DX9CaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCaptureService"/> class.
    /// </summary>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="DX9CaptureOptions.Default"/>.</param>
    public DX9ScreenCaptureService(DX9CaptureOptions? options = null)
    {
        Options = options ?? DX9CaptureOptions.Default;

        D3D9.Direct3DCreate9Ex(out _direct3D9).CheckError();
        _screenCaptures = new ScreenCaptureCache<DX9ScreenCapture, DX9CaptureOptions>(this, (display, captureOptions) => new DX9ScreenCapture(_direct3D9, display, captureOptions), capture => capture.Options);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Checks if Direct3D 9Ex can be used on this system.
    /// </summary>
    /// <returns><c>true</c> if it's supported; otherwise, <c>false</c>.</returns>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return CanCreateDirect3D9();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Separate, so the DirectX types are only loaded on Windows.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CanCreateDirect3D9()
    {
        bool created = D3D9.Direct3DCreate9Ex(out IDirect3D9Ex? direct3D9).Success;
        using (direct3D9)
            return created;
    }

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        lock (_direct3D9Lock)
        {
            ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

            Dictionary<string, GraphicsCard> graphicsCards = new(StringComparer.Ordinal);
            for (int i = 0; i < _direct3D9.AdapterCount; i++)
            {
                // The first adapter of each card creates it.
                string id = DX9Devices.GetGraphicsCardId(_direct3D9, i);
                if (!graphicsCards.ContainsKey(id))
                    graphicsCards.Add(id, DX9Devices.CreateGraphicsCard(_direct3D9, i));
            }

            return graphicsCards.Values;
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        // Read right away, so the D3D9 instance isn't used outside the lock.
        lock (_direct3D9Lock)
        {
            ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

            WindowsDisplayDevices devices = WindowsDisplayDevices.Query();
            List<Display> displays = [];

            for (int i = 0; i < _direct3D9.AdapterCount; i++)
            {
                if (!string.Equals(DX9Devices.GetGraphicsCardId(_direct3D9, i), graphicsCard.Id, StringComparison.Ordinal))
                    continue;

                if (displays.Count == 0)
                    graphicsCard = graphicsCard with { Index = i };

                displays.Add(DX9Devices.CreateDisplay(_direct3D9, i, graphicsCard, devices));
            }

            return displays;
        }
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    /// <remarks>
    /// A new capture gets the <see cref="Options"/> of the service, an existing one keeps its own.
    /// </remarks>
    public DX9ScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="DX9ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// There's one capture per <see cref="Display.Id"/>. An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="DX9ScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public DX9ScreenCapture GetScreenCapture(Display display, DX9CaptureOptions options)
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
        // The captures use the D3D9 instance too.
        if (!_screenCaptures.DisposeAll()) return;

        lock (_direct3D9Lock)
            try { _direct3D9.Dispose(); } catch { /**/ }
    }

    #endregion
}