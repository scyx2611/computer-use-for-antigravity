namespace ComputerUse.Native;

internal sealed class CaptureEngine
{
    private readonly CaptureBackendChain backends;

    public CaptureEngine()
        : this(new CaptureBackendChain())
    {
    }

    internal CaptureEngine(CaptureBackendChain backends)
    {
        this.backends = backends;
    }

    public ScreenshotResult Capture(IntPtr hwnd, WindowRectData rect)
    {
        return backends.Capture(hwnd, rect);
    }
}
