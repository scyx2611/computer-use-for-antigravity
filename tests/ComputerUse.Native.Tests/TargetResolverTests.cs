using System.Text.Json.Nodes;

namespace ComputerUse.Native.Tests;

internal static class TargetResolverTests
{
    public static void Run()
    {
        var exact = Snapshot(1, "Save", "Button", "saveButton", true);
        var contains = Snapshot(2, "Save as", "Button", "saveAsButton", true);
        var selected = TargetResolver.FindSnapshot(
            [exact, contains],
            TargetDescriptor.FromAction(new JsonObject { ["name"] = "Save", ["role"] = "Button" }));
        TestAssert.Equal(1, selected!.Snapshot.Id, "a unique exact selector must win over a contains match");

        var duplicateError = TestAssert.Throws<ComputerUseException>(
            () => TargetResolver.FindSnapshot(
                [
                    Snapshot(3, "Open", "Button", "", true),
                    Snapshot(4, "Open", "Button", "", false)
                ],
                TargetDescriptor.FromAction(new JsonObject { ["name"] = "Open", ["role"] = "Button" })),
            "duplicate exact selectors must fail as ambiguous");
        TestAssert.Equal("AMBIGUOUS_TARGET", duplicateError.Code, "ambiguous target error code");
        var candidates = duplicateError.Details?["candidates"] as JsonArray;
        TestAssert.True(candidates is not null, "ambiguous target must include candidates");
        TestAssert.Equal(2, candidates!.Count, "ambiguous target candidate count");
        TestAssert.Equal(3, candidates[0]!["element_id"]!.GetValue<int>(), "candidate order must be deterministic");

        var automationId = TargetResolver.FindSnapshot(
            [
                Snapshot(5, "Run", "Button", "runA", true),
                Snapshot(6, "Run", "Button", "runB", true)
            ],
            TargetDescriptor.FromAction(new JsonObject { ["automation_id"] = "runB" }));
        TestAssert.Equal(6, automationId!.Snapshot.Id, "a unique automation id must resolve deterministically");

        var noMatch = TargetResolver.FindSnapshot(
            [Snapshot(7, "Cancel", "Button", "", true)],
            TargetDescriptor.FromAction(new JsonObject { ["name"] = "Save" }));
        TestAssert.True(noMatch is null, "unmatched selectors must return no match");
    }

    private static UiElementSnapshot Snapshot(
        int id,
        string name,
        string role,
        string automationId,
        bool enabled)
    {
        return new UiElementSnapshot
        {
            Id = id,
            Name = name,
            Role = role,
            AutomationId = automationId,
            Bounds = [id * 10, id * 10, 100, 30],
            IsEnabled = enabled,
            RuntimeId = $"runtime-{id}"
        };
    }
}
