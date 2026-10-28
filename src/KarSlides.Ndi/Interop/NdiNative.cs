using System.Reflection;
using System.Runtime.InteropServices;

namespace KarSlides.Ndi.Interop;

/// <summary>
/// Hand-written P/Invoke for the send part of NDI SDK 6.
/// Struct layouts mirror Processing.NDI.structs.h / Processing.NDI.Send.h (x64, natural alignment).
/// The runtime DLL is resolved from NDI_RUNTIME_DIR_V6 (NDI license forbids shipping it).
/// </summary>
internal static unsafe class NdiNative
{
    private const string Lib = "Processing.NDI.Lib.x64.dll";
    public const string RuntimeEnvVar = "NDI_RUNTIME_DIR_V6";

    private static IntPtr _handle;
    private static readonly object Sync = new();

    public static string? LoadedFrom { get; private set; }

    /// <summary>
    /// Loads the NDI runtime and calls NDIlib_initialize. Throws with a clear message if it is missing.
    /// </summary>
    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_handle != IntPtr.Zero) return;

            var dir = Environment.GetEnvironmentVariable(RuntimeEnvVar);
            if (string.IsNullOrWhiteSpace(dir))
                throw new InvalidOperationException(
                    $"NDI runtime not found: environment variable {RuntimeEnvVar} is not set. Install NDI 6 Runtime (or NDI Tools).");

            var path = Path.Combine(dir, Lib);
            if (!File.Exists(path))
                throw new InvalidOperationException($"NDI runtime not found: '{path}' does not exist ({RuntimeEnvVar}='{dir}').");

            _handle = NativeLibrary.Load(path);
            LoadedFrom = path;
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
                name == Lib ? _handle : IntPtr.Zero);

            if (!NDIlib_is_supported_CPU())
                throw new InvalidOperationException("NDI: CPU is not supported (SSE4.2 required).");
            if (!NDIlib_initialize())
                throw new InvalidOperationException("NDI: NDIlib_initialize() returned false.");
        }
    }

    public static string Version => Marshal.PtrToStringUTF8(NDIlib_version()) ?? "?";

    // ---- Enums / constants ----

    public static uint FourCC(char a, char b, char c, char d) => a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    public static readonly uint FourCC_BGRA = FourCC('B', 'G', 'R', 'A');
    public static readonly uint FourCC_BGRX = FourCC('B', 'G', 'R', 'X');
    public static readonly uint FourCC_FLTP = FourCC('F', 'L', 'T', 'p');

    public const int FrameFormatProgressive = 1;
    public const long TimecodeSynthesize = long.MaxValue;

    // ---- Structs ----

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;   // const char*, UTF-8
        public IntPtr p_groups;     // const char*, NULL = default
        public byte clock_video;    // C bool
        public byte clock_audio;    // C bool
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres, yres;
        public uint FourCC;
        public int frame_rate_N, frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV3
    {
        public int sample_rate;
        public int no_channels;
        public int no_samples;
        public long timecode;
        public uint FourCC;
        public IntPtr p_data;
        public int channel_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    // ---- Functions ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_initialize();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_is_supported_CPU();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(SendCreate* p_create_settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr p_instance);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr p_instance, VideoFrameV2* p_video_data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_async_v2(IntPtr p_instance, VideoFrameV2* p_video_data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_audio_v3(IntPtr p_instance, AudioFrameV3* p_audio_data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NDIlib_send_get_no_connections(IntPtr p_instance, uint timeout_in_ms);
}
