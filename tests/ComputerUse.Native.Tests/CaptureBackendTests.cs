namespace ComputerUse.Native.Tests;

internal static class CaptureBackendTests
{
    public static void Run()
    {
        var rect = new WindowRectData { X = 0, Y = 0, Width = 320, Height = 200 };
        var chain = new CaptureBackendChain(
            new FakeCaptureBackend("windows_graphics_capture", CaptureAttempt.Failure("windows_graphics_capture", "unsupported")),
            new FakeCaptureBackend("print_window", CaptureAttempt.Success("print_window", [1, 2, 3], 320, 200)),
            new FakeCaptureBackend("bitblt", CaptureAttempt.Failure("bitblt", "not reached")));

        var fallback = chain.Capture(IntPtr.Zero, rect);
        TestAssert.Equal("print_window", fallback.Diagnostics.Backend, "the chain must use the first successful backend");
        TestAssert.True(fallback.Diagnostics.FallbackUsed, "using the second backend must be marked as fallback");
        TestAssert.Equal(1, fallback.Diagnostics.Errors.Count, "successful fallback must retain prior errors");
        TestAssert.Equal("windows_graphics_capture", fallback.Diagnostics.Errors[0].Backend, "fallback diagnostics backend");
        TestAssert.True(fallback.Base64 is not null && fallback.Hash is not null, "successful capture must include image and hash");

        var failed = new CaptureBackendChain(
            new FakeCaptureBackend("windows_graphics_capture", CaptureAttempt.Failure("windows_graphics_capture", "unsupported")),
            new FakeCaptureBackend("print_window", CaptureAttempt.Failure("print_window", "blocked")));
        var allFailed = failed.Capture(IntPtr.Zero, rect);
        TestAssert.True(allFailed.Base64 is null, "failed capture must not return image data");
        TestAssert.Equal(2, allFailed.Diagnostics.Errors.Count, "all backend failures must be reported");
        TestAssert.True(allFailed.Error!.Contains("print_window: blocked", StringComparison.Ordinal), "capture error must identify the failing backend");
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        private readonly CaptureAttempt result;

        public FakeCaptureBackend(string name, CaptureAttempt result)
        {
            Name = name;
            this.result = result;
        }

        public string Name { get; }

        public CaptureAttempt Capture(IntPtr hwnd, WindowRectData rect) => result;
    }
}
