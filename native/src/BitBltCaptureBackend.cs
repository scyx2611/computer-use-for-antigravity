using System.Runtime.InteropServices;

namespace ComputerUse.Native;

internal sealed class BitBltCaptureBackend : GdiCaptureBackend
{
    private const uint RasterOperationSourceCopy = 0x00CC0020;
    private const uint RasterOperationCaptureBlit = 0x40000000;

    public BitBltCaptureBackend()
        : base("bitblt")
    {
    }

    protected override bool Render(
        IntPtr hwnd,
        IntPtr windowDc,
        IntPtr memoryDc,
        int width,
        int height)
    {
        return BitBlt(
            memoryDc,
            0,
            0,
            width,
            height,
            windowDc,
            0,
            0,
            RasterOperationSourceCopy | RasterOperationCaptureBlit);
    }

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
}
