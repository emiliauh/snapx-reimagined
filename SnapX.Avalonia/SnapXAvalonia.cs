using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapX.Avalonia;

public class SnapXAvalonia : Core.SnapXL
{
    // Encoding the captured frame to PNG and having Avalonia decode it right
    // back adds a redundant compress/decompress pass to every capture. Copy
    // the decoded pixels directly into a WriteableBitmap instead.
    public WriteableBitmap ConvertImageSharpImgToAvalonia(Image image)
    {
        Image<Rgba32> rgba = image as Image<Rgba32> ?? image.CloneAs<Rgba32>();
        try
        {
            var bitmap = new WriteableBitmap(
                new PixelSize(rgba.Width, rgba.Height),
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Unpremul);

            using var framebuffer = bitmap.Lock();
            unsafe
            {
                byte* dest = (byte*)framebuffer.Address;
                int rowBytes = framebuffer.RowBytes;
                int copyBytes = rgba.Width * 4;

                for (int y = 0; y < rgba.Height; y++)
                {
                    var rowSpan = rgba.DangerousGetPixelRowMemory(y).Span;
                    var destSpan = new Span<byte>(dest + (long)y * rowBytes, copyBytes);
                    MemoryMarshal.AsBytes(rowSpan).CopyTo(destSpan);
                }
            }

            return bitmap;
        }
        finally
        {
            if (!ReferenceEquals(rgba, image))
            {
                rgba.Dispose();
            }
        }
    }
}
