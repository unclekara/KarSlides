using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using KarSlides.Core.Preview;

namespace KarSlides.App;

/// <summary>JPEG for the live preview through GDI+ (part of Windows, no extra package). Preview thread only.</summary>
internal sealed class GdiJpegEncoder : IPreviewEncoder
{
    private readonly ImageCodecInfo _codec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public byte[] Encode(byte[] bgra, int width, int height, int quality)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            using var stream = new MemoryStream(width * height / 4);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(stream, _codec, parameters);
            return stream.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
