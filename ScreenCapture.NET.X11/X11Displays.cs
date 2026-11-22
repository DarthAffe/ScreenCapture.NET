using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Enumerates the displays of an X server: one per RandR 1.5 monitor, or one per screen without monitors.
/// Shared by the service and the captures, so both see the same displays.
/// </summary>
internal static class X11Displays
{
    #region Constants

    // The base block of an EDID is 128 bytes; the property is read in 32-bit units.
    private const int EDID_LENGTH = 128;

    private const int EDID_DESCRIPTORS_OFFSET = 54;
    private const int EDID_DESCRIPTOR_LENGTH = 18;
    private const int EDID_DESCRIPTOR_COUNT = 4;
    private const byte EDID_MONITOR_NAME_TAG = 0xFC;

    private static ReadOnlySpan<byte> EdidHeader => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    #endregion

    #region Methods

    /// <summary>
    /// Gets if the server is XWayland, by its extension or (older versions) the names of its outputs (XWAYLAND0, ...).
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection broke meanwhile.</exception>
    public static bool IsXWayland(X11DisplayHandle display)
    {
        if (X11.XQueryExtension(display, "XWAYLAND", out _, out _, out _)) return true;

        foreach (X11Display x11Display in GetDisplays(display))
            if (x11Display.Display.DeviceName.StartsWith("XWAYLAND", StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// Gets the graphics card of an X server, which is the server itself.
    /// </summary>
    public static GraphicsCard GetGraphicsCard(X11DisplayHandle display)
    {
        string name = Marshal.PtrToStringAnsi(X11.XDisplayString(display)) ?? string.Empty;
        return new GraphicsCard(name, 0, name, 0, 0);
    }

    /// <summary>
    /// Gets all displays of the server: the active monitors of every screen, or the whole screen if it has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection broke meanwhile.</exception>
    public static List<X11Display> GetDisplays(X11DisplayHandle display)
    {
        GraphicsCard graphicsCard = GetGraphicsCard(display);
        bool supportsMonitors = SupportsMonitors(display);
        CULong edidAtom = supportsMonitors ? X11.XInternAtom(display, "EDID", onlyIfExists: true) : default;

        List<X11Display> displays = [];
        int screenCount = X11.XScreenCount(display);
        for (int screenNumber = 0; screenNumber < screenCount; screenNumber++)
        {
            nint screen = X11.XScreenOfDisplay(display, screenNumber);

            int count = displays.Count;
            if (supportsMonitors)
                AddMonitors(display, X11.XRootWindowOfScreen(screen), screenNumber, graphicsCard, edidAtom, displays);

            if (displays.Count == count)
                displays.Add(new X11Display(new Display($"{graphicsCard.Id}/{screenNumber}", displays.Count, $"Screen {screenNumber}", string.Empty,
                                                        0, 0, X11.XWidthOfScreen(screen), X11.XHeightOfScreen(screen), ImageRotation.None, graphicsCard),
                                            screenNumber, default));
        }

        // A broken connection reads no monitors, which looks like a server without them.
        if (display.IsConnectionLost)
            throw new InvalidOperationException($"Reading the displays failed: the connection to the X server '{graphicsCard.Id}' was lost.");

        return displays;
    }

    /// <summary>
    /// Finds the display with the given id as it is now.
    /// </summary>
    /// <returns>The display; <c>null</c> if the server has no display with the id (anymore).</returns>
    /// <exception cref="InvalidOperationException">The connection broke meanwhile.</exception>
    public static X11Display? Find(X11DisplayHandle display, string id)
    {
        foreach (X11Display x11Display in GetDisplays(display))
            if (string.Equals(x11Display.Display.Id, id, StringComparison.Ordinal))
                return x11Display;

        return null;
    }

    /// <summary>
    /// Reads where a monitor is now. One round trip, without the names (see <see cref="GetDisplays"/>).
    /// </summary>
    /// <returns><c>false</c> if the screen of the root window has no active monitor with the name (anymore), or the connection broke.</returns>
    public static unsafe bool TryGetMonitorRegion(X11DisplayHandle display, CULong root, CULong monitor, out CaptureRegion region)
    {
        X11.XRRMonitorInfo* monitors = X11.XRRGetMonitors(display, root, getActive: true, out int count);
        try
        {
            for (int i = 0; i < count; i++)
                if (monitors[i].name.Value == monitor.Value)
                {
                    region = new CaptureRegion(monitors[i].x, monitors[i].y, monitors[i].width, monitors[i].height);
                    return true;
                }

            region = default;
            return false;
        }
        finally
        {
            if (monitors != null)
                X11.XRRFreeMonitors(monitors);
        }
    }

    private static bool SupportsMonitors(X11DisplayHandle display)
    {
        try
        {
            // Checked first: XRRQueryVersion prints a warning to stderr if the server has no RandR at all.
            return X11.XRRQueryExtension(display, out _, out _)
                && (X11.XRRQueryVersion(display, out int major, out int minor) != 0)
                && ((major > 1) || ((major == 1) && (minor >= 5)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static unsafe void AddMonitors(X11DisplayHandle display, CULong root, int screenNumber, GraphicsCard graphicsCard, CULong edidAtom, List<X11Display> displays)
    {
        X11.XRRMonitorInfo* monitors = X11.XRRGetMonitors(display, root, getActive: true, out int count);
        if (monitors == null) return;

        try
        {
            for (int i = 0; i < count; i++)
            {
                X11.XRRMonitorInfo monitor = monitors[i];
                if ((monitor.width <= 0) || (monitor.height <= 0)) continue;

                string name = GetAtomName(display, monitor.name);

                // Monitors defined by a client (xrandr --setmonitor ... none) have no output.
                string friendlyName = (monitor.noutput > 0) && (edidAtom.Value != 0) ? ReadMonitorName(display, monitor.outputs[0], edidAtom) : string.Empty;

                displays.Add(new X11Display(new Display($"{graphicsCard.Id}/{screenNumber}/{name}", displays.Count, name, friendlyName,
                                                        monitor.x, monitor.y, monitor.width, monitor.height, ImageRotation.None, graphicsCard),
                                            screenNumber, monitor.name));
            }
        }
        finally
        {
            X11.XRRFreeMonitors(monitors);
        }
    }

    private static string GetAtomName(X11DisplayHandle display, CULong atom)
    {
        nint name = X11.XGetAtomName(display, atom);
        if (name == 0) return string.Empty;

        try
        {
            return Marshal.PtrToStringAnsi(name) ?? string.Empty;
        }
        finally
        {
            _ = X11.XFree(name);
        }
    }

    private static unsafe string ReadMonitorName(X11DisplayHandle display, CULong output, CULong edidAtom)
    {
        int result = X11.XRRGetOutputProperty(display, output, edidAtom, new CLong(0), new CLong(EDID_LENGTH / 4), delete: false, pending: false, X11.ANY_PROPERTY_TYPE,
                                              out _, out int format, out CULong itemCount, out _, out nint data);
        try
        {
            if ((result != X11.SUCCESS) || (data == 0) || (format != 8)) return string.Empty;

            return GetMonitorName(new ReadOnlySpan<byte>((void*)data, (int)Math.Min(itemCount.Value, EDID_LENGTH)));
        }
        finally
        {
            if (data != 0)
                _ = X11.XFree(data);
        }
    }

    /// <summary>
    /// Reads the monitor name (display descriptor 0xFC) from the base block of an EDID.
    /// </summary>
    /// <returns>The name; empty if the EDID is invalid or has no name.</returns>
    internal static string GetMonitorName(ReadOnlySpan<byte> edid)
    {
        if ((edid.Length < EDID_LENGTH) || !edid.StartsWith(EdidHeader)) return string.Empty;

        for (int i = 0; i < EDID_DESCRIPTOR_COUNT; i++)
        {
            ReadOnlySpan<byte> descriptor = edid.Slice(EDID_DESCRIPTORS_OFFSET + (i * EDID_DESCRIPTOR_LENGTH), EDID_DESCRIPTOR_LENGTH);

            // Display descriptors start with a pixel clock of 0; detailed timings don't.
            if ((descriptor[0] != 0) || (descriptor[1] != 0) || (descriptor[3] != EDID_MONITOR_NAME_TAG)) continue;

            // Up to 13 characters, ended by a line feed and padded with spaces.
            ReadOnlySpan<byte> text = descriptor[5..];
            int end = text.IndexOf((byte)'\n');
            if (end >= 0) text = text[..end];

            return Encoding.ASCII.GetString(text).Trim();
        }

        return string.Empty;
    }

    #endregion
}

/// <summary>
/// A display of an X server with what a capture needs to read it.
/// </summary>
/// <param name="Display">The display.</param>
/// <param name="Screen">The number of the X screen that shows it.</param>
/// <param name="Monitor">The name (atom) of its RandR monitor; 0 if the display is the whole screen.</param>
internal readonly record struct X11Display(Display Display, int Screen, CULong Monitor);
