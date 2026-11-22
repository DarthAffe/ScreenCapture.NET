// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

// user32.dll is a system library; never load it from anywhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ScreenCapture.NET;

/// <summary>
/// Reads the ids and names Windows uses for graphics cards and monitors (<c>QueryDisplayConfig</c>), so all Windows backends report the same ids.
/// </summary>
/// <remarks>
/// The ids are the device interface paths of the adapter and the monitor. They survive reboots and driver updates,
/// but change if the card is moved to another slot or the monitor to another port.
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
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    // DISPLAYCONFIG_SDR_WHITE_LEVEL.SDRWhiteLevel: 1000 is 80 nits.
    private const float SDR_WHITE_LEVEL_UNIT_NITS = 80f / 1000f;

    #endregion

    #region Properties & Fields

    private readonly Dictionary<string, Monitor> _monitors;

    #endregion

    #region Constructors

    private WindowsDisplayDevices(Dictionary<string, Monitor> monitors)
    {
        this._monitors = monitors;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Reads the monitors of the active displays. If that fails, the ids fall back to the device names.
    /// </summary>
    public static WindowsDisplayDevices Query()
    {
        Dictionary<string, Monitor> monitors = new(StringComparer.OrdinalIgnoreCase);

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
                monitors.Add(deviceName, new Monitor(string.IsNullOrEmpty(devicePath) ? deviceName : devicePath, friendlyName, new MonitorTarget(path.TargetAdapterId, path.TargetId)));
            }

            break;
        }

        return new WindowsDisplayDevices(monitors);
    }

    /// <summary>
    /// Gets the id of the adapter with the given LUID: its device interface path, or the LUID in hex if it has none.
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
    /// <returns>The device interface path of the monitor (or the device name) and its name (empty if unknown).</returns>
    public (string Id, string FriendlyName) GetMonitor(string deviceName)
        => _monitors.TryGetValue(deviceName, out Monitor? monitor) ? (monitor.Id, monitor.FriendlyName) : (deviceName, string.Empty);

    /// <summary>
    /// Gets the target of the display with the given GDI device name, to read values of the monitor later.
    /// </summary>
    /// <returns>The target, or <c>null</c> if the display isn't known.</returns>
    public MonitorTarget? GetTarget(string deviceName)
        => _monitors.TryGetValue(deviceName, out Monitor? monitor) ? monitor.Target : null;

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

    #region Types

    private sealed record Monitor(string Id, string FriendlyName, MonitorTarget Target);

    /// <summary>
    /// The connection of a monitor to its adapter (a path target of <c>QueryDisplayConfig</c>). Valid until the display configuration changes.
    /// </summary>
    public readonly struct MonitorTarget
    {
        private readonly Luid _adapterId;
        private readonly uint _id;

        internal MonitorTarget(Luid adapterId, uint id)
        {
            this._adapterId = adapterId;
            this._id = id;
        }

        /// <summary>
        /// Reads the "SDR content brightness" of the monitor while it shows HDR. Takes a few µs.
        /// </summary>
        /// <param name="nits">The luminance of SDR white in nits (80 without HDR).</param>
        /// <returns><c>false</c> if the target isn't known anymore.</returns>
        public bool TryGetSdrWhiteLevel(out float nits)
        {
            SdrWhiteLevel request = new() { Header = CreateHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(SdrWhiteLevel), _adapterId, _id) };
            bool success = (DisplayConfigGetDeviceInfo(&request.Header) == ERROR_SUCCESS) && (request.SDRWhiteLevel > 0);

            nits = success ? request.SDRWhiteLevel * SDR_WHITE_LEVEL_UNIT_NITS : 0;
            return success;
        }
    }

    #endregion

    #region Interop types (wingdi.h)

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint SDRWhiteLevel;
    }

    #endregion
}
