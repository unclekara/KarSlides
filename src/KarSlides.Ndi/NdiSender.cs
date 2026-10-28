using System.Runtime.InteropServices;
using System.Text;
using KarSlides.Ndi.Interop;
using Microsoft.Extensions.Logging;

namespace KarSlides.Ndi;

/// <summary>One NDI send instance. Video and audio may be sent from different threads.</summary>
internal sealed unsafe class NdiSender : IDisposable
{
    private readonly ILogger _log;
    private readonly IntPtr _name;
    private IntPtr _instance;

    public string Name { get; }

    public NdiSender(string name, ILogger log)
    {
        _log = log;
        NdiRuntime.EnsureLoaded();
        Name = name;

        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        _name = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _name, bytes.Length);

        // Own timer paces the output, so NDI must not clock video.
        var create = new NdiNative.SendCreate { p_ndi_name = _name, clock_video = 0, clock_audio = 0 };
        _instance = NdiNative.NDIlib_send_create(&create);
        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException($"NDIlib_send_create('{name}') returned NULL");
        _log.LogInformation("NDI sender '{Name}' created, runtime {Version}", name, NdiRuntime.Version);
    }

    /// <summary>
    /// Sends one BGRA frame. Async: returns immediately, NDI keeps using the buffer until the next send call
    /// or <see cref="FlushAsync"/>. Sync: the buffer may be reused after return.
    /// </summary>
    public void SendVideo(IntPtr data, int width, int height, int stride, int fpsN, int fpsD, long timecode, bool async)
    {
        var frame = new NdiNative.VideoFrameV2
        {
            xres = width,
            yres = height,
            FourCC = NdiNative.FourCC_BGRA,
            frame_rate_N = fpsN,
            frame_rate_D = fpsD,
            frame_format_type = NdiNative.FrameFormatProgressive,
            timecode = timecode,
            p_data = data,
            line_stride_in_bytes = stride,
        };
        if (async) NdiNative.NDIlib_send_send_video_async_v2(_instance, &frame);
        else NdiNative.NDIlib_send_send_video_v2(_instance, &frame);
    }

    /// <summary>Sends planar float audio (FLTP): channel after channel, channelStride bytes apart. Copied by NDI.</summary>
    public void SendAudio(IntPtr planar, int sampleRate, int channels, int samples, int channelStrideBytes, long timecode)
    {
        var frame = new NdiNative.AudioFrameV3
        {
            sample_rate = sampleRate,
            no_channels = channels,
            no_samples = samples,
            timecode = timecode,
            FourCC = NdiNative.FourCC_FLTP,
            p_data = planar,
            channel_stride_in_bytes = channelStrideBytes,
        };
        NdiNative.NDIlib_send_send_audio_v3(_instance, &frame);
    }

    /// <summary>Waits until NDI has released the buffer of the last async send.</summary>
    public void FlushAsync()
    {
        if (_instance != IntPtr.Zero) NdiNative.NDIlib_send_send_video_async_v2(_instance, null);
    }

    public int Connections => _instance == IntPtr.Zero ? 0 : NdiNative.NDIlib_send_get_no_connections(_instance, 0);

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            NdiNative.NDIlib_send_destroy(_instance);
            _instance = IntPtr.Zero;
            _log.LogInformation("NDI sender '{Name}' destroyed", Name);
        }
        Marshal.FreeHGlobal(_name);
    }
}
