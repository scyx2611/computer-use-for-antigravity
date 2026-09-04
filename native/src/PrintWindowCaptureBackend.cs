using System.Runtime.InteropServices;

namespace ComputerUse.Native;

internal sealed class PrintWindowCaptureBackend : GdiCaptureBackend
{
    private const uint PrintWindowRenderFullContent = 0x00000002;

    public PrintWindowCaptureBackend()
        : base("print_window")
    {
    }

    protected override bool Render(
        IntPtr hwnd,
        IntPtr windowDc,
        IntPtr memoryDc,
        int width,
        int height)
    {
        return PrintWindow(hwnd, memoryDc, PrintWindowRenderFullContent);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);
}
