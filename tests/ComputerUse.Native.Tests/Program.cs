namespace ComputerUse.Native.Tests;

internal static class Program
{
    private static readonly (string Name, Action Body)[] Tests =
    [
        ("coordinate spaces", CoordinateTransformTests.Run),
        ("target resolution", TargetResolverTests.Run),
        ("capture backend chain", CaptureBackendTests.Run),
        ("state expiry and drift", StateManagerTests.Run),
        ("postconditions", PostconditionTests.Run),
        ("workflow runner", WorkflowRunnerTests.Run)
    ];

    public static int Main()
    {
        var failures = 0;
        foreach (var test in Tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT total={Tests.Length} passed={Tests.Length - failures} failed={failures}");
        return failures == 0 ? 0 : 1;
    }
}
