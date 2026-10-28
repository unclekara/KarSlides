using System.Runtime.InteropServices;
using KarSlides.Core.Audio;
using Microsoft.Win32;

namespace KarSlides.Audio;

/// <summary>
/// Playback devices and the per-application device of Settings > Sound > Volume mixer. The latter is
/// the undocumented WinRT factory Windows.Media.Internal.AudioPolicyConfig (SetPersistedDefaultAudioEndpoint),
/// as EarTrumpet and SoundSwitch use it. Its IID changed in Windows 10 21H2, both are tried; the vtable after
/// IInspectable is the same. A running stream moves to the new device within milliseconds (measured).
/// Also reports Windows audio enhancements per device: they delay PowerPoint audio in the loopback.
/// </summary>
public sealed unsafe class WindowsAudioRouter : IAudioRouter
{
    private const string PolicyClass = "Windows.Media.Internal.AudioPolicyConfig";
    private static readonly Guid[] PolicyIids =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"), // Windows 10 21H2+, Windows 11
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // Windows 10 1803..21H1
    ];

    // IUnknown (3) + IInspectable (3) + 19 methods not used here.
    private const int SlotSet = 25, SlotGet = 26;
    private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const int ERender = 0, EConsole = 0, EMultimedia = 1, DeviceStateActive = 1;

    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string DisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    public IReadOnlyList<PlaybackDevice> Devices()
    {
        var result = new List<PlaybackDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            var defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var def) == 0)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }
            Check(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var collection), "EnumAudioEndpoints");
            try
            {
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var device);
                    try
                    {
                        device.GetId(out var id);
                        result.Add(new PlaybackDevice(id, FriendlyName(device) ?? id, id == defaultId, EnhancementsEnabled(id)));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Route(uint pid, string? deviceId)
    {
        var factory = Factory();
        try
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)VTable(factory, SlotSet);
            var hs = deviceId is null ? IntPtr.Zero : CreateHString(DevicePrefix + deviceId + RenderSuffix);
            try
            {
                Check(set(factory, pid, ERender, EConsole, hs), "SetPersistedDefaultAudioEndpoint");
                Check(set(factory, pid, ERender, EMultimedia, hs), "SetPersistedDefaultAudioEndpoint");
            }
            finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
        }
        finally { Marshal.Release(factory); }
    }

    public string Routed(uint pid)
    {
        var factory = Factory();
        try
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)VTable(factory, SlotGet);
            IntPtr hs;
            Check(get(factory, pid, ERender, EMultimedia, &hs), "GetPersistedDefaultAudioEndpoint");
            try
            {
                var s = ReadHString(hs);
                if (s.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)) s = s[DevicePrefix.Length..];
                if (s.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) s = s[..^RenderSuffix.Length];
                return s;
            }
            finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>Windows 11 keeps the switch in FxProperties, older builds in Properties; 1 = enhancements off.</summary>
    private static bool EnhancementsEnabled(string id)
    {
        var guid = id[(id.LastIndexOf('.') + 1)..]; // "{0.0.0.00000000}.{guid}"
        using var key = Registry.LocalMachine.OpenSubKey($@"{RenderKey}\{guid}");
        if (key is null) return false;
        using var props = key.OpenSubKey("Properties");
        using var fx = key.OpenSubKey("FxProperties");
        var disabled = Convert.ToInt32(fx?.GetValue(DisableSysFx) ?? props?.GetValue(DisableSysFx) ?? 0) == 1;
        return fx?.GetValueNames().Length > 0 && !disabled;
    }

    private static IntPtr Factory()
    {
        var cls = CreateHString(PolicyClass);
        try
        {
            var last = 0;
            foreach (var iid in PolicyIids)
            {
                var g = iid;
                last = RoGetActivationFactory(cls, ref g, out var factory);
                if (last == 0) return factory;
            }
            throw new COMException($"per-application playback device is not supported by this Windows (AudioPolicyConfig 0x{last:X8})", last);
        }
        finally { WindowsDeleteString(cls); }
    }

    private static IntPtr VTable(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 }; // PKEY_Device_FriendlyName
            var pv = new PropVariant();
            if (store.GetValue(ref key, ref pv) != 0) return null;
            try { return pv.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.Pointer) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static IntPtr CreateHString(string s)
    {
        Check(WindowsCreateString(s, (uint)s.Length, out var hs), "WindowsCreateString");
        return hs;
    }

    private static string ReadHString(IntPtr hs)
    {
        if (hs == IntPtr.Zero) return "";
        var p = WindowsGetStringRawBuffer(hs, out var length);
        return Marshal.PtrToStringUni(p, (int)length) ?? "";
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new COMException($"{what} failed: 0x{hr:X8}", hr);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Fmtid;
        public int Pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    }
}
