using System.Runtime.InteropServices;
using KarSlides.Core.Model;
using KarSlides.Core.Monitors;

namespace KarSlides.Capture;

/// <summary>Enumerates monitors for <see cref="MonitorMatcher"/> (device name, stable DeviceID, bounds).</summary>
public static class DisplayMonitors
{
    public static List<MonitorDescriptor> Enumerate()
    {
        var result = new List<MonitorDescriptor>();
        var targets = DisplayTargets();
        MonitorEnumProc callback = (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMon, ref mi)) return true;

            var adapter = FindAdapter(mi.szDevice);
            var monitor = FindMonitor(mi.szDevice);
            var adapterName = adapter?.DeviceString ?? "";
            var r = mi.rcMonitor;
            targets.TryGetValue(mi.szDevice, out var target);
            var connection = target.Connection;
            var isVirtual = connection == MonitorConnection.Virtual || LooksVirtual(adapterName, monitor?.DeviceString ?? "");
            if (isVirtual) connection = MonitorConnection.Virtual;
            result.Add(new MonitorDescriptor(
                mi.szDevice,
                monitor?.DeviceID ?? "",
                adapterName,
                isVirtual,
                (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                target.Name ?? "",
                connection));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    public static MonitorTarget ToTarget(MonitorDescriptor m) => new(m.DeviceName, m.Left, m.Top, m.Width, m.Height);

    /// <summary>HMONITOR for capture, by device name (\\.\DISPLAYn).</summary>
    internal static IntPtr FindHandle(string deviceName)
    {
        var found = IntPtr.Zero;
        MonitorEnumProc callback = (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi) && mi.szDevice.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMon;
                return false;
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found != IntPtr.Zero ? found : throw new InvalidOperationException($"Monitor {deviceName} is not connected");
    }

    /// <summary>IddCx virtual display drivers usually say so in the adapter or monitor name.</summary>
    private static bool LooksVirtual(string adapter, string monitor)
    {
        string[] marks = ["virtual", "idd", "parsec", "usbmmidd", "amyuni"];
        return marks.Any(m => adapter.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                              monitor.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static DISPLAY_DEVICE? FindAdapter(string deviceName)
    {
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) return null;
            if (dd.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) return dd;
        }
    }

    private static DISPLAY_DEVICE? FindMonitor(string deviceName)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(deviceName, 0, ref dd, 0) ? dd : null;
    }

    /// <summary>
    /// EDID name and connector of every active display, by GDI device name (\\.\DISPLAYn), through the
    /// DisplayConfig API. Empty on failure: names are a convenience, never a reason to fail.
    /// </summary>
    private static Dictionary<string, (string? Name, MonitorConnection Connection)> DisplayTargets()
    {
        var result = new Dictionary<string, (string?, MonitorConnection)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

            for (var i = 0; i < pathCount; i++)
            {
                var p = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = p.sourceInfo.adapterId;
                source.header.id = p.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = p.targetInfo.adapterId;
                target.header.id = p.targetInfo.id;
                var name = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.monitorFriendlyDeviceName : null;
                result[source.viewGdiDeviceName] = (string.IsNullOrWhiteSpace(name) ? null : name, ToConnection(p.targetInfo.outputTechnology));
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            // very old Windows: no names
        }
        return result;
    }

    private static MonitorConnection ToConnection(uint tech) => tech switch
    {
        0 => MonitorConnection.Vga,
        4 or 6 => MonitorConnection.Dvi, // DVI, LVDS
        5 => MonitorConnection.Hdmi,
        10 => MonitorConnection.DisplayPort,
        11 or 0x80000000 => MonitorConnection.Internal, // embedded DisplayPort, internal
        13 or 15 => MonitorConnection.Wireless, // Miracast
        16 or 17 => MonitorConnection.Virtual, // indirect wired / virtual (IddCx)
        _ => MonitorConnection.Other,
    };

    // ---- Interop ----

    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshNumerator;
        public uint refreshDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>64 bytes: type, id, adapter and a 48-byte union of target/source/desktop mode (not read here).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
