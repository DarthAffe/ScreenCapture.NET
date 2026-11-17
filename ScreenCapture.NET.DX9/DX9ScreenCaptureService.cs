using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="DX9ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, DX9CaptureOptions)"/>.
/// </remarks>
public sealed partial class DX9ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly IDirect3D9Ex _direct3D9;
    private readonly Dictionary<string, DX9ScreenCapture> _screenCaptures = new(StringComparer.Ordinal);

    private bool _isDisposed;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public DX9CaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCaptureService"/> class.
    /// </summary>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="DX9CaptureOptions.Default"/>.</param>
    public DX9ScreenCaptureService(DX9CaptureOptions? options = null)
    {
        Options = options ?? DX9CaptureOptions.Default;

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
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </remarks>
    public DX9ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display.Id, out DX9ScreenCapture? screenCapture))
            _screenCaptures.Add(display.Id, screenCapture = new DX9ScreenCapture(_direct3D9, display, Options));
        return screenCapture;
    }

    /// <summary>
    /// Creates a <see cref="DX9ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="DX9ScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public DX9ScreenCapture GetScreenCapture(Display display, DX9CaptureOptions options)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (_screenCaptures.TryGetValue(display.Id, out DX9ScreenCapture? screenCapture))
        {
            if (!screenCapture.Options.Equals(options))
                throw new InvalidOperationException($"The capture of the display '{display.Id}' already exists with other options ({screenCapture.Options}).");

            return screenCapture;
        }

        _screenCaptures.Add(display.Id, screenCapture = new DX9ScreenCapture(_direct3D9, display, options));
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