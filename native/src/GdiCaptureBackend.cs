using System.Runtime.InteropServices;

namespace ComputerUse.Native;

internal abstract class GdiCaptureBackend : ICaptureBackend
{
    protected GdiCaptureBackend(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public CaptureAttempt Capture(IntPtr hwnd, WindowRectData rect)
    {
        var validationError = CaptureDimensions.Validate(rect);
        if (validationError is not null)
        {
            return CaptureAttempt.Failure(Name, validationError);
        }

        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            return CaptureAttempt.Failure(
                Name,
                $"GetWindowDC failed (Win32 error {Marshal.GetLastWin32Error()}).");
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
                return CaptureAttempt.Failure(
                    Name,
                    $"Unable to allocate a capture surface (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero)
            {
                return CaptureAttempt.Failure(
                    Name,
                    $"SelectObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (!Render(hwnd, windowDc, memoryDc, rect.Width, rect.Height))
            {
                return CaptureAttempt.Failure(
                    Name,
                    $"{Name} failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            var pngBytes = CaptureImageEncoder.EncodeHBitmap(bitmap, rect.Width, rect.Height);
            return CaptureAttempt.Success(Name, pngBytes, rect.Width, rect.Height);
        }
        catch (Exception exception)
        {
            return CaptureAttempt.Failure(Name, $"{Name} encoding failed: {exception.Message}");
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

    protected abstract bool Render(
        IntPtr hwnd,
        IntPtr windowDc,
        IntPtr memoryDc,
        int width,
        int height);

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
}
