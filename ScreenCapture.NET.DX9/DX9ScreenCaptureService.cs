using System;
using System.Collections.Generic;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX9ScreenCapture"/>.
/// </summary>
public class DX9ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly IDirect3D9 _direct3D9;
    private readonly Dictionary<Display, DX9ScreenCapture> _screenCaptures = new();

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCaptureService"/> class.
    /// </summary>
    public DX9ScreenCaptureService()
    {
        _direct3D9 = D3D9.Direct3DCreate9();
    }

    ~DX9ScreenCaptureService() => Dispose();

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        Dictionary<int, GraphicsCard> graphicsCards = new();
        for (int i = 0; i < _direct3D9.AdapterCount; i++)
        {
            AdapterIdentifier adapterIdentifier = _direct3D9.GetAdapterIdentifier(i);
            if (!graphicsCards.ContainsKey(adapterIdentifier.DeviceId))
                graphicsCards.Add(adapterIdentifier.DeviceId, new GraphicsCard(i, adapterIdentifier.Description, adapterIdentifier.VendorId, adapterIdentifier.DeviceId));
        }

        return graphicsCards.Values;
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        for (int i = 0; i < _direct3D9.AdapterCount; i++)
        {
            AdapterIdentifier adapterIdentifier = _direct3D9.GetAdapterIdentifier(i);
            if (adapterIdentifier.DeviceId == graphicsCard.DeviceId)
            {
                DisplayMode displayMode = _direct3D9.GetAdapterDisplayMode(i);
                yield return new Display(i, adapterIdentifier.DeviceName, displayMode.Width, displayMode.Height, Rotation.None, graphicsCard);
            }
        }
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);
    public DX9ScreenCapture GetScreenCapture(Display display)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        if (!_screenCaptures.TryGetValue(display, out DX9ScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new DX9ScreenCapture(_direct3D9, display));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (DX9ScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        try { _direct3D9.Dispose(); } catch { /**/ }

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    #endregion
}