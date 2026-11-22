using System;
using System.Collections.Generic;
using HPPH;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// Enumerates the DXGI adapters and outputs as <see cref="GraphicsCard"/>s and <see cref="Display"/>s, for the services and for captures that read their display again.
/// </summary>
/// <remarks>
/// A factory keeps showing the adapters it was created with. If they changed since (e.g. after a driver update, or a graphics card was added or removed),
/// the methods enumerate with a new factory, so the shared one of a service can be kept.
/// </remarks>
internal static class DXGIDevices
{
    #region Methods

    /// <summary>
    /// Reads all adapters of the factory.
    /// </summary>
    public static List<GraphicsCard> GetGraphicsCards(IDXGIFactory1 factory) => WithCurrentFactory(factory, ReadGraphicsCards);

    private static List<GraphicsCard> ReadGraphicsCards(IDXGIFactory1 factory)
    {
        List<GraphicsCard> graphicsCards = [];
        for (int i = 0; ; i++)
        {
            bool found = factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success;
            using (adapter)
            {
                if (!found) return graphicsCards;

                graphicsCards.Add(CreateGraphicsCard(adapter, i));
            }
        }
    }

    /// <summary>
    /// Reads all outputs of the adapter with the id of <paramref name="graphicsCard"/>.
    /// </summary>
    /// <returns>The displays, with the card as it is now (its index can have changed); empty if the card isn't there anymore.</returns>
    public static List<Display> GetDisplays(IDXGIFactory1 factory, GraphicsCard graphicsCard) => WithCurrentFactory(factory, current => ReadDisplays(current, graphicsCard));

    private static List<Display> ReadDisplays(IDXGIFactory1 factory, GraphicsCard graphicsCard)
    {
        List<Display> displays = [];

        // The card is found by its id: its index can point to another adapter by now (e.g. after an adapter was added or removed).
        if (FindAdapter(factory, graphicsCard.Id) is not (IDXGIAdapter1 adapter, int index)) return displays;
        using IDXGIAdapter1 _ = adapter;

        // The captures open the adapter by its index.
        graphicsCard = graphicsCard with { Index = index };
        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();

        for (int i = 0; ; i++)
        {
            bool found = adapter.EnumOutputs((uint)i, out IDXGIOutput output).Success;
            using (output)
            {
                if (!found) return displays;

                displays.Add(CreateDisplay(output.Description, i, graphicsCard, devices));
            }
        }
    }

    // The caller disposes the adapter.
    private static (IDXGIAdapter1 Adapter, int Index)? FindAdapter(IDXGIFactory1 factory, string id)
    {
        for (int i = 0; ; i++)
        {
            if (!factory.EnumAdapters1((uint)i, out IDXGIAdapter1 adapter).Success)
            {
                adapter?.Dispose();
                return null;
            }

            if (string.Equals(WindowsDisplayDevices.GetGraphicsCardId(adapter.Description1.Luid), id, StringComparison.Ordinal)) return (adapter, i);

            adapter.Dispose();
        }
    }

    public static GraphicsCard CreateGraphicsCard(IDXGIAdapter1 adapter, int index)
    {
        AdapterDescription1 description = adapter.Description1;
        return new GraphicsCard(WindowsDisplayDevices.GetGraphicsCardId(description.Luid), index, description.Description, (int)description.VendorId, (int)description.DeviceId);
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
    /// <returns>
    /// The adapter and the output (the caller disposes them), the current state of the display and the target of its monitor (<c>null</c> if unknown);
    /// <c>null</c> if the display isn't connected.
    /// </returns>
    public static (IDXGIAdapter1 Adapter, IDXGIOutput Output, Display Display, WindowsDisplayDevices.MonitorTarget? Target)? FindDisplay(IDXGIFactory1 factory, string id)
        => FindDisplay(factory, (description, devices) => string.Equals(devices.GetMonitor(description.DeviceName).Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Finds the output showing the monitor with the given handle (<c>HMONITOR</c>), on any adapter.
    /// </summary>
    /// <returns>See <see cref="FindDisplay(IDXGIFactory1, string)"/>; <c>null</c> if no output shows the monitor.</returns>
    public static (IDXGIAdapter1 Adapter, IDXGIOutput Output, Display Display, WindowsDisplayDevices.MonitorTarget? Target)? FindDisplay(IDXGIFactory1 factory, nint monitor)
        => FindDisplay(factory, (description, _) => description.Monitor == monitor);

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output, Display Display, WindowsDisplayDevices.MonitorTarget? Target)? FindDisplay(IDXGIFactory1 factory,
                                                                                                                                         Func<OutputDescription, WindowsDisplayDevices, bool> matches)
        // The adapter and output keep their factory alive: the new factory can be released when they're returned.
        => WithCurrentFactory(factory, current => ReadDisplay(current, matches));

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output, Display Display, WindowsDisplayDevices.MonitorTarget? Target)? ReadDisplay(IDXGIFactory1 factory,
                                                                                                                                          Func<OutputDescription, WindowsDisplayDevices, bool> matches)
    {
        WindowsDisplayDevices devices = WindowsDisplayDevices.Query();

        for (int adapterIndex = 0; ; adapterIndex++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                if (!factory.EnumAdapters1((uint)adapterIndex, out adapter).Success) return null;

                for (int outputIndex = 0; ; outputIndex++)
                {
                    IDXGIOutput? output = null;
                    try
                    {
                        if (!adapter.EnumOutputs((uint)outputIndex, out output).Success) break;

                        OutputDescription description = output.Description;
                        if (!matches(description, devices)) continue;

                        (IDXGIAdapter1, IDXGIOutput, Display, WindowsDisplayDevices.MonitorTarget?) result
                            = (adapter, output, CreateDisplay(description, outputIndex, CreateGraphicsCard(adapter, adapterIndex), devices), devices.GetTarget(description.DeviceName));

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

    // Runs the enumeration with the factory, or with a new one if the adapters changed since the factory was created.
    private static T WithCurrentFactory<T>(IDXGIFactory1 factory, Func<IDXGIFactory1, T> enumerate)
    {
        if (factory.IsCurrent) return enumerate(factory);

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? current).CheckError();
        using (current)
            return enumerate(current!);
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
