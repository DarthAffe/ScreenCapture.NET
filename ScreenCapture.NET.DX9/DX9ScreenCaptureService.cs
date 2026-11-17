using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX9ScreenCapture"/>.
/// </summary>
public sealed partial class DX9ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly IDirect3D9Ex _direct3D9;
    private readonly Dictionary<string, DX9ScreenCapture> _screenCaptures = new(StringComparer.Ordinal);

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCaptureService"/> class.
    /// </summary>
    public DX9ScreenCaptureService()
    {
        D3D9.Direct3DCreate9Ex(out _direct3D9).CheckError();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Dictionary<string, GraphicsCard> graphicsCards = new(StringComparer.Ordinal);
        for (int i = 0; i < _direct3D9.AdapterCount; i++)
        {
            string id = WindowsDisplayDevices.GetGraphicsCardId(_direct3D9.GetAdapterLuid(i));
            if (graphicsCards.ContainsKey(id)) continue;

            AdapterIdentifier adapterIdentifier = _direct3D9.GetAdapterIdentifier(i);
            graphicsCards.Add(id, new GraphicsCard(id, i, adapterIdentifier.Description, adapterIdentifier.VendorId, adapterIdentifier.DeviceId));
        }

        return graphicsCards.Values;
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();
        bool found = false;

        for (int i = 0; i < _direct3D9.AdapterCount; i++)
        {
            if (!string.Equals(WindowsDisplayDevices.GetGraphicsCardId(_direct3D9.GetAdapterLuid(i)), graphicsCard.Id, StringComparison.Ordinal))
                continue;

            if (!found)
            {
                graphicsCard = graphicsCard with { Index = i };
                found = true;
            }

            AdapterIdentifier adapterIdentifier = _direct3D9.GetAdapterIdentifier(i);
            DisplayMode displayMode = _direct3D9.GetAdapterDisplayMode(i);
            (string id, string friendlyName) = devices.GetMonitor(adapterIdentifier.DeviceName);
            (int x, int y) = GetPosition(_direct3D9.GetAdapterMonitor(i));

            yield return new Display(id, i, adapterIdentifier.DeviceName, friendlyName, x, y, displayMode.Width, displayMode.Height, Rotation.None, graphicsCard);
        }
    }

    private static (int x, int y) GetPosition(nint monitor)
    {
        MonitorInfo info = new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(monitor, ref info) ? (info.MonitorLeft, info.MonitorTop) : (0, 0);
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    public DX9ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display.Id, out DX9ScreenCapture? screenCapture))
            _screenCaptures.Add(display.Id, screenCapture = new DX9ScreenCapture(_direct3D9, display));
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

        _isDisposed = true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    #endregion

    #region Interop types (winuser.h)

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public int MonitorLeft;
        public int MonitorTop;
        public int MonitorRight;
        public int MonitorBottom;
        public int WorkLeft;
        public int WorkTop;
        public int WorkRight;
        public int WorkBottom;
        public uint Flags;
    }

    #endregion
}