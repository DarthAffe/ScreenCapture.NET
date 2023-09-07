using System;
using System.Collections.Generic;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX11ScreenCapture"/>.
/// </summary>
public class DX11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly IDXGIFactory1 _factory;
    private readonly Dictionary<Display, DX11ScreenCapture> _screenCaptures = new();

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

    ~DX11ScreenCaptureService() => Dispose();

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        int i = 0;
        while (_factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success)
        {
            yield return new GraphicsCard(i, adapter.Description1.Description, adapter.Description1.VendorId, adapter.Description1.DeviceId);
            adapter.Dispose();
            i++;
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        using IDXGIAdapter1? adapter = _factory.GetAdapter1(graphicsCard.Index);

        int i = 0;
        while (adapter.EnumOutputs(i, out IDXGIOutput output).Success)
        {
            int width = output.Description.DesktopCoordinates.Right - output.Description.DesktopCoordinates.Left;
            int height = output.Description.DesktopCoordinates.Bottom - output.Description.DesktopCoordinates.Top;
            yield return new Display(i, output.Description.DeviceName, width, height, GetRotation(output.Description.Rotation), graphicsCard);
            output.Dispose();
            i++;
        }
    }

    private static Rotation GetRotation(ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => Rotation.Rotation90,
        ModeRotation.Rotate180 => Rotation.Rotation180,
        ModeRotation.Rotate270 => Rotation.Rotation270,
        _ => Rotation.None
    };

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);
    public DX11ScreenCapture GetScreenCapture(Display display)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        if (!_screenCaptures.TryGetValue(display, out DX11ScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new DX11ScreenCapture(_factory, display));
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

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    #endregion
}