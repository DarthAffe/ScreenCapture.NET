using System;
using System.Collections.Generic;
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

    private readonly IDXGIFactory1 _factory;
    private readonly Dictionary<string, DX11ScreenCapture> _screenCaptures = new(StringComparer.Ordinal);

    private bool _isDisposed;

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
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        for (int i = 0; ; i++)
        {
            bool found = _factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success;
            using (adapter)
            {
                if (!found) yield break;

                AdapterDescription1 description = adapter.Description1;
                yield return new GraphicsCard(WindowsDisplayDevices.GetGraphicsCardId(description.Luid), i, description.Description, description.VendorId, description.DeviceId);
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // The card is found by its id: its index can point to another adapter by now (e.g. after an adapter was added or removed).
        int index = FindAdapterIndex(graphicsCard.Id);
        if (index < 0) yield break;

        // The capture opens the adapter by its index.
        graphicsCard = graphicsCard with { Index = index };
        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();

        using IDXGIAdapter1 adapter = _factory.GetAdapter1(index);

        for (int i = 0; ; i++)
        {
            bool found = adapter.EnumOutputs(i, out IDXGIOutput output).Success;
            using (output)
            {
                if (!found) yield break;

                OutputDescription description = output.Description;
                (string id, string friendlyName) = devices.GetMonitor(description.DeviceName);
                int x = description.DesktopCoordinates.Left;
                int y = description.DesktopCoordinates.Top;
                int width = description.DesktopCoordinates.Right - x;
                int height = description.DesktopCoordinates.Bottom - y;

                yield return new Display(id, i, description.DeviceName, friendlyName, x, y, width, height, GetRotation(description.Rotation), graphicsCard);
            }
        }
    }

    private int FindAdapterIndex(string id)
    {
        for (int i = 0; ; i++)
        {
            bool found = _factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success;
            using (adapter)
            {
                if (!found) return -1;
                if (string.Equals(WindowsDisplayDevices.GetGraphicsCardId(adapter.Description1.Luid), id, StringComparison.Ordinal)) return i;
            }
        }
    }

    private static Rotation GetRotation(ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => Rotation.Rotate90,
        ModeRotation.Rotate180 => Rotation.Rotate180,
        ModeRotation.Rotate270 => Rotation.Rotate270,
        _ => Rotation.None
    };

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </remarks>
    public DX11ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display.Id, out DX11ScreenCapture? screenCapture))
            _screenCaptures.Add(display.Id, screenCapture = new DX11ScreenCapture(_factory, display, Options));
        return screenCapture;
    }

    /// <summary>
    /// Creates a <see cref="DX11ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="DX11ScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public DX11ScreenCapture GetScreenCapture(Display display, DX11CaptureOptions options)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (_screenCaptures.TryGetValue(display.Id, out DX11ScreenCapture? screenCapture))
        {
            if (!screenCapture.Options.Equals(options))
                throw new InvalidOperationException($"The capture of the display '{display.Id}' already exists with other options ({screenCapture.Options}).");

            return screenCapture;
        }

        _screenCaptures.Add(display.Id, screenCapture = new DX11ScreenCapture(_factory, display, options));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (DX11ScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        try { _factory.Dispose(); } catch { /**/ }

        _isDisposed = true;
    }

    #endregion
}