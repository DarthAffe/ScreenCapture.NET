using System;
using HPPH;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Creates <see cref="GraphicsCard"/>s and <see cref="Display"/>s from DXGI adapters and outputs, for the service and for captures that read their display again.
/// </summary>
internal static class DX11Devices
{
    #region Methods

    public static GraphicsCard CreateGraphicsCard(IDXGIAdapter1 adapter, int index)
    {
        AdapterDescription1 description = adapter.Description1;
        return new GraphicsCard(WindowsDisplayDevices.GetGraphicsCardId(description.Luid), index, description.Description, description.VendorId, description.DeviceId);
    }

    public static Display CreateDisplay(OutputDescription description, int index, GraphicsCard graphicsCard, WindowsDisplayDevices devices)
    {
        (string id, string friendlyName) = devices.GetMonitor(description.DeviceName);
        int x = description.DesktopCoordinates.Left;
        int y = description.DesktopCoordinates.Top;
        int width = description.DesktopCoordinates.Right - x;
        int height = description.DesktopCoordinates.Bottom - y;

        return new Display(id, index, description.DeviceName, friendlyName, x, y, width, height, GetRotation(description.Rotation), graphicsCard);
    }

    /// <summary>
    /// Finds the output showing the display with the given id, on any adapter.
    /// </summary>
    /// <returns>The adapter and the output (the caller disposes them) and the current state of the display; <c>null</c> if the display isn't connected.</returns>
    public static (IDXGIAdapter1 Adapter, IDXGIOutput Output, Display Display)? FindDisplay(IDXGIFactory1 factory, string id)
    {
        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();

        for (int adapterIndex = 0; ; adapterIndex++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                if (!factory.EnumAdapters1(adapterIndex, out adapter).Success) return null;

                for (int outputIndex = 0; ; outputIndex++)
                {
                    IDXGIOutput? output = null;
                    try
                    {
                        if (!adapter.EnumOutputs(outputIndex, out output).Success) break;

                        OutputDescription description = output.Description;
                        if (!string.Equals(devices.GetMonitor(description.DeviceName).Id, id, StringComparison.Ordinal)) continue;

                        (IDXGIAdapter1, IDXGIOutput, Display) result = (adapter, output, CreateDisplay(description, outputIndex, CreateGraphicsCard(adapter, adapterIndex), devices));

                        // Owned by the caller now.
                        adapter = null;
                        output = null;

                        return result;
                    }
                    finally
                    {
                        output?.Dispose();
                    }
                }
            }
            finally
            {
                adapter?.Dispose();
            }
        }
    }

    private static ImageRotation GetRotation(ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => ImageRotation.Rotate90,
        ModeRotation.Rotate180 => ImageRotation.Rotate180,
        ModeRotation.Rotate270 => ImageRotation.Rotate270,
        _ => ImageRotation.None
    };

    #endregion
}
