using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ComputerUse.Native;

internal sealed class CaptureEngine
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const uint RasterOperationSourceCopy = 0x00CC0020;
    private const uint RasterOperationCaptureBlit = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationDeviceContext,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceDeviceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    public ScreenshotResult Capture(IntPtr hwnd, WindowRectData rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return new ScreenshotResult
            {
                Error = "The target window has no drawable area."
            };
        }

        // Avoid an accidental multi-gigabyte allocation if a malformed or
        // unusual window reports an enormous rectangle.
        if (rect.Width > 8192 || rect.Height > 8192)
        {
            return new ScreenshotResult
            {
                Error = "The target window is larger than the MVP capture limit (8192x8192)."
            };
        }

        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            return new ScreenshotResult
            {
                Error = $"GetWindowDC failed (Win32 error {Marshal.GetLastWin32Error()})."
            };
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, rect.Width, rect.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return new ScreenshotResult
                {
                    Error = $"Unable to allocate a capture surface (Win32 error {Marshal.GetLastWin32Error()})."
                };
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero)
            {
                return new ScreenshotResult
                {
                    Error = $"SelectObject failed (Win32 error {Marshal.GetLastWin32Error()})."
                };
            }

            var rendered = PrintWindow(hwnd, memoryDc, PrintWindowRenderFullContent);
            if (!rendered)
            {
                // PrintWindow is the preferred MVP path. The fallback helps
                // ordinary top-level windows whose provider declines it.
                rendered = BitBlt(
                    memoryDc,
                    0,
                    0,
                    rect.Width,
                    rect.Height,
                    windowDc,
                    0,
                    0,
                    RasterOperationSourceCopy | RasterOperationCaptureBlit);
            }

            if (!rendered)
            {
                return new ScreenshotResult
                {
                    Error = $"PrintWindow and BitBlt both failed (Win32 error {Marshal.GetLastWin32Error()})."
                };
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();

            return new ScreenshotResult
            {
                Base64 = Convert.ToBase64String(bytes),
                Hash = Convert.ToHexString(SHA256.HashData(bytes))
            };
        }
        catch (Exception exception)
        {
            return new ScreenshotResult
            {
                Error = $"Screenshot encoding failed: {exception.Message}"
            };
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }

            _ = ReleaseDC(hwnd, windowDc);
        }
    }
}
