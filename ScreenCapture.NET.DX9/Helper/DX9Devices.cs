using System;
using System.Runtime.InteropServices;
using HPPH;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Creates <see cref="GraphicsCard"/>s and <see cref="Display"/>s from D3D9 adapters.
/// </summary>
/// <remarks>
/// D3D9 has one adapter per display. A graphics card is identified by the LUID of its adapters and has the index of its first one.
/// </remarks>
internal static partial class DX9Devices
{
    #region Methods

    public static string GetGraphicsCardId(IDirect3D9Ex direct3D9, int adapter) => WindowsDisplayDevices.GetGraphicsCardId(direct3D9.GetAdapterLuid((uint)adapter));

    /// <summary>
    /// Creates the graphics card the given adapter belongs to.
    /// </summary>
    public static GraphicsCard CreateGraphicsCard(IDirect3D9Ex direct3D9, int adapter)
    {
        string id = GetGraphicsCardId(direct3D9, adapter);

        int index = adapter;
        for (int i = 0; i < adapter; i++)
            if (string.Equals(GetGraphicsCardId(direct3D9, i), id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }

        AdapterIdentifier identifier = direct3D9.GetAdapterIdentifier((uint)index);
        return new GraphicsCard(id, index, identifier.Description, identifier.VendorId, identifier.DeviceId);
    }

    public static Display CreateDisplay(IDirect3D9Ex direct3D9, int adapter, GraphicsCard graphicsCard, WindowsDisplayDevices devices)
    {
        AdapterIdentifier identifier = direct3D9.GetAdapterIdentifier((uint)adapter);
        DisplayMode displayMode = direct3D9.GetAdapterDisplayMode((uint)adapter);
        (string id, string friendlyName) = devices.GetMonitor(identifier.DeviceName);
        (int x, int y) = GetPosition(direct3D9.GetAdapterMonitor((uint)adapter));

        return new Display(id, adapter, identifier.DeviceName, friendlyName, x, y, (int)displayMode.Width, (int)displayMode.Height, ImageRotation.None, graphicsCard);
    }

    /// <summary>
    /// Finds the adapter showing the display with the given id.
    /// </summary>
    /// <returns>The current state of the display; <c>null</c> if the display isn't connected.</returns>
    public static Display? FindDisplay(IDirect3D9Ex direct3D9, string id)
    {
        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();

        for (int i = 0; i < direct3D9.AdapterCount; i++)
            if (string.Equals(devices.GetMonitor(direct3D9.GetAdapterIdentifier((uint)i).DeviceName).Id, id, StringComparison.Ordinal))
                return CreateDisplay(direct3D9, i, CreateGraphicsCard(direct3D9, i), devices);

        return null;
    }

    private static (int x, int y) GetPosition(nint monitor)
    {
        MonitorInfo info = new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(monitor, ref info) ? (info.MonitorLeft, info.MonitorTop) : (0, 0);
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
