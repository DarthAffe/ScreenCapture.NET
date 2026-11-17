// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

// user32.dll is a system library; never load it from anywhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ScreenCapture.NET;

/// <summary>
/// Reads the ids and names Windows uses for graphics cards and monitors (<c>QueryDisplayConfig</c>). Shared by the Windows backends, so they report the same ids.
/// </summary>
/// <remarks>
/// The ids are the device interface paths of the adapter and the monitor. They survive reboots, driver updates and changes of the GDI device names (<c>\\.\DISPLAY1</c>);
/// they change if the card is moved to another slot or the monitor to another port.
/// </remarks>
internal sealed unsafe partial class WindowsDisplayDevices
{
    #region Constants

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private const int QUERY_ATTEMPTS = 3;

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4;

    #endregion

    #region Properties & Fields

    private readonly Dictionary<string, (string Id, string FriendlyName)> _monitors;

    #endregion

    #region Constructors

    private WindowsDisplayDevices(Dictionary<string, (string Id, string FriendlyName)> monitors)
    {
        this._monitors = monitors;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Reads the monitors of the active displays. If that fails (e.g. without a desktop), all ids fall back to the values the backends report.
    /// </summary>
    public static WindowsDisplayDevices Query()
    {
        Dictionary<string, (string Id, string FriendlyName)> monitors = new(StringComparer.OrdinalIgnoreCase);

        for (int attempt = 0; attempt < QUERY_ATTEMPTS; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                break;

            PathInfo[] paths = new PathInfo[pathCount];
            ModeInfo[] modes = new ModeInfo[modeCount];

            int result;
            fixed (PathInfo* pathsPtr = paths)
            fixed (ModeInfo* modesPtr = modes)
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, pathsPtr, ref modeCount, modesPtr, 0);

            if (result == ERROR_INSUFFICIENT_BUFFER) continue;
            if (result != ERROR_SUCCESS) break;

            // A display in clone mode has a path per monitor: the first one wins.
            foreach (PathInfo path in paths.AsSpan(0, (int)pathCount))
            {
                string? deviceName = GetSourceName(path.SourceAdapterId, path.SourceId);
                if (string.IsNullOrEmpty(deviceName) || monitors.ContainsKey(deviceName)) continue;

                (string devicePath, string friendlyName) = GetTargetName(path.TargetAdapterId, path.TargetId);
                monitors.Add(deviceName, (string.IsNullOrEmpty(devicePath) ? deviceName : devicePath, friendlyName));
            }

            break;
        }

        return new WindowsDisplayDevices(monitors);
    }

    /// <summary>
    /// Gets the id of the adapter with the given LUID: its device interface path, or the LUID (hexadecimal) if Windows has none.
    /// </summary>
    public static string GetGraphicsCardId(long luid)
    {
        AdapterName request = new() { Header = CreateHeader(DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME, sizeof(AdapterName), Luid.From(luid), 0) };
        string devicePath = DisplayConfigGetDeviceInfo(&request.Header) == ERROR_SUCCESS ? ReadString(request.AdapterDevicePath, 128) : string.Empty;

        return string.IsNullOrEmpty(devicePath) ? luid.ToString("X16", CultureInfo.InvariantCulture) : devicePath;
    }

    /// <summary>
    /// Gets the id and the friendly name of the monitor of the display with the given GDI device name.
    /// </summary>
    /// <returns>The device interface path of the monitor (the device name if Windows has none) and its name (empty if unknown).</returns>
    public (string Id, string FriendlyName) GetMonitor(string deviceName)
        => _monitors.TryGetValue(deviceName, out (string Id, string FriendlyName) monitor) ? monitor : (deviceName, string.Empty);

    private static string? GetSourceName(Luid adapterId, uint id)
    {
        SourceDeviceName request = new() { Header = CreateHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(SourceDeviceName), adapterId, id) };
        return DisplayConfigGetDeviceInfo(&request.Header) == ERROR_SUCCESS ? ReadString(request.ViewGdiDeviceName, 32) : null;
    }

    private static (string DevicePath, string FriendlyName) GetTargetName(Luid adapterId, uint id)
    {
        TargetDeviceName request = new() { Header = CreateHeader(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, sizeof(TargetDeviceName), adapterId, id) };
        if (DisplayConfigGetDeviceInfo(&request.Header) != ERROR_SUCCESS) return (string.Empty, string.Empty);

        return (ReadString(request.MonitorDevicePath, 128), ReadString(request.MonitorFriendlyDeviceName, 64));
    }

    private static DeviceInfoHeader CreateHeader(uint type, int size, Luid adapterId, uint id)
        => new() { Type = type, Size = (uint)size, AdapterId = adapterId, Id = id };

    private static string ReadString(char* buffer, int length)
    {
        ReadOnlySpan<char> value = new(buffer, length);
        int end = value.IndexOf('\0');
        return new string(end < 0 ? value : value[..end]);
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, PathInfo* pathArray, ref uint numModeInfoArrayElements, ModeInfo* modeInfoArray, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);

    #endregion

    #region Interop types (wingdi.h)

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public static Luid From(long value) => new() { LowPart = (uint)value, HighPart = (int)(value >> 32) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;

        public Luid TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterName
    {
        public DeviceInfoHeader Header;
        public fixed char AdapterDevicePath[128];
    }

    #endregion
}
