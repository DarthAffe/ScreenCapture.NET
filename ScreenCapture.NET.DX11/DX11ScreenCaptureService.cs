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
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        for (int i = 0; ; i++)
        {
            bool found = _factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success;
            using (adapter)
            {
                if (!found) yield break;

                AdapterDescription1 description = adapter.Description1;
                yield return new GraphicsCard(i, description.Description, description.VendorId, description.DeviceId);
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using IDXGIAdapter1? adapter = _factory.GetAdapter1(graphicsCard.Index);

        for (int i = 0; ; i++)
        {
            bool found = adapter.EnumOutputs(i, out IDXGIOutput output).Success;
            using (output)
            {
                if (!found) yield break;

                OutputDescription description = output.Description;
                int width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
                int height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;
                yield return new Display(i, description.DeviceName, width, height, GetRotation(description.Rotation), graphicsCard);
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
    public DX11ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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