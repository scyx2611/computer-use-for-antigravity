namespace ComputerUse.Native;

internal interface ICaptureBackend
{
    string Name { get; }

    CaptureAttempt Capture(IntPtr hwnd, WindowRectData rect);
}

internal sealed class CaptureAttempt
{
    private CaptureAttempt(
        string backend,
        byte[]? pngBytes,
        int width,
        int height,
        string? error)
    {
        Backend = backend;
        PngBytes = pngBytes;
        Width = width;
        Height = height;
        Error = error;
    }

    public string Backend { get; }

    public byte[]? PngBytes { get; }

    public int Width { get; }

    public int Height { get; }

    public string? Error { get; }

    public bool Succeeded => PngBytes is { Length: > 0 };

    public static CaptureAttempt Success(string backend, byte[] pngBytes, int width, int height)
    {
        return new CaptureAttempt(backend, pngBytes, width, height, null);
    }

    public static CaptureAttempt Failure(string backend, string error)
    {
        return new CaptureAttempt(backend, null, 0, 0, error);
    }
}

internal sealed class CaptureBackendFailure
{
    public CaptureBackendFailure(string backend, string error)
    {
        Backend = backend;
        Error = error;
    }

    public string Backend { get; }

    public string Error { get; }
}

internal static class CaptureDimensions
{
    public const int MaximumDimension = 8192;

    public static string? Validate(WindowRectData rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return "The target window has no drawable area.";
        }

        if (rect.Width > MaximumDimension || rect.Height > MaximumDimension)
        {
            return $"The target window is larger than the MVP capture limit ({MaximumDimension}x{MaximumDimension}).";
        }

        return null;
    }
}
