using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComputerUse.Native;

internal static class CaptureImageEncoder
{
    public static byte[] EncodeBgra32(byte[] pixels, int width, int height, int stride)
    {
        if (pixels is null || width <= 0 || height <= 0 || stride < width * 4)
        {
            throw new ArgumentException("BGRA32 pixels have invalid dimensions or stride.");
        }

        var requiredBytes = checked(stride * height);
        if (pixels.Length < requiredBytes)
        {
            throw new ArgumentException("BGRA32 pixel data is shorter than the supplied stride and height.");
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return EncodePng(source);
    }

    public static byte[] EncodeHBitmap(IntPtr bitmap, int width, int height)
    {
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return EncodePng(source);
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
