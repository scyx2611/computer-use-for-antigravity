using System.IO;

namespace ComputerUse.Native.Tests;

internal static class BrowserTests
{
    public static void Run()
    {
        BrowserUrlPolicy.Validate("about:blank");
        BrowserUrlPolicy.Validate("http://127.0.0.1:8080/form");
        BrowserUrlPolicy.ValidateInitialArguments(["--no-first-run", "https://example.test/"]);

        var rejectedScheme = TestAssert.Throws<ComputerUseException>(
            () => BrowserUrlPolicy.Validate("javascript:alert(1)"),
            "javascript URLs must be rejected");
        TestAssert.Equal("UNSUPPORTED_URL_SCHEME", rejectedScheme.Code, "unsafe URL error code");

        var rejectedProfile = TestAssert.Throws<ComputerUseException>(
            () => BrowserUrlPolicy.Validate("file:///C:/secret.txt"),
            "file URLs must be rejected");
        TestAssert.Equal("UNSUPPORTED_URL_SCHEME", rejectedProfile.Code, "file URL error code");

        var root = Path.Combine(Path.GetTempPath(), $"computer-use-browser-test-{Guid.NewGuid():N}");
        try
        {
            var profiles = new BrowserProfileManager(root);
            var profile = profiles.CreateEphemeral("unit-test");
            TestAssert.True(Directory.Exists(profile), "ephemeral profile should be created");
            TestAssert.True(
                Path.GetFullPath(profile).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "ephemeral profile should remain under the managed root");
            profiles.DeleteEphemeral(profile);
            TestAssert.True(!Directory.Exists(profile), "ephemeral profile should be deleted");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
