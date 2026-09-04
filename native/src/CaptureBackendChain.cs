using System.Security.Cryptography;

namespace ComputerUse.Native;

internal sealed class CaptureBackendChain
{
    private readonly IReadOnlyList<ICaptureBackend> backends;

    public CaptureBackendChain()
        : this(
            new WindowsGraphicsCaptureBackend(),
            new PrintWindowCaptureBackend(),
            new BitBltCaptureBackend())
    {
    }

    internal CaptureBackendChain(params ICaptureBackend[] backends)
    {
        if (backends is null || backends.Length == 0)
        {
            throw new ArgumentException("At least one capture backend is required.", nameof(backends));
        }

        this.backends = backends;
    }

    public ScreenshotResult Capture(IntPtr hwnd, WindowRectData rect)
    {
        var failures = new List<CaptureBackendFailure>();
        for (var index = 0; index < backends.Count; index++)
        {
            var backend = backends[index];
            CaptureAttempt attempt;

            try
            {
                attempt = backend.Capture(hwnd, rect);
            }
            catch (Exception exception)
            {
                attempt = CaptureAttempt.Failure(
                    backend.Name,
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            if (attempt.Succeeded)
            {
                var pngBytes = attempt.PngBytes!;
                var hash = Convert.ToHexString(SHA256.HashData(pngBytes));
                return new ScreenshotResult
                {
                    Base64 = Convert.ToBase64String(pngBytes),
                    Hash = hash,
                    Diagnostics = new CaptureDiagnostics
                    {
                        Backend = attempt.Backend,
                        Width = attempt.Width,
                        Height = attempt.Height,
                        Hash = hash,
                        FallbackUsed = index > 0,
                        Errors = failures.Select(ToFailure).ToArray()
                    }
                };
            }

            failures.Add(new CaptureBackendFailure(
                attempt.Backend,
                attempt.Error ?? "The capture backend did not return an image."));
        }

        var message = failures.Count == 0
            ? "All capture backends failed without diagnostic information."
            : string.Join("; ", failures.Select(failure => $"{failure.Backend}: {failure.Error}"));

        return new ScreenshotResult
        {
            Error = message,
            Diagnostics = new CaptureDiagnostics
            {
                Width = 0,
                Height = 0,
                FallbackUsed = failures.Count > 1,
                Errors = failures.Select(ToFailure).ToArray()
            }
        };
    }

    private static CaptureFailure ToFailure(CaptureBackendFailure failure)
    {
        return new CaptureFailure
        {
            Backend = failure.Backend,
            Error = failure.Error
        };
    }
}
