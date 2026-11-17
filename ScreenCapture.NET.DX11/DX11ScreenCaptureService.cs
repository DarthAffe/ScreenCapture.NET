using System;
using System.Collections.Generic;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX11ScreenCapture"/>.
/// </summary>
public sealed class DX11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly IDXGIFactory1 _factory;
    private readonly Dictionary<string, DX11ScreenCapture> _screenCaptures = new(StringComparer.Ordinal);

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX11ScreenCaptureService"/> class.
    /// </summary>
    public DX11ScreenCaptureService()
    {
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
    public DX11ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display.Id, out DX11ScreenCapture? screenCapture))
            _screenCaptures.Add(display.Id, screenCapture = new DX11ScreenCapture(_factory, display));
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