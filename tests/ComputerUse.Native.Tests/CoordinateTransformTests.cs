using System.Text.Json.Nodes;

namespace ComputerUse.Native.Tests;

internal static class CoordinateTransformTests
{
    public static void Run()
    {
        var window = new WindowRectData { X = 100, Y = 200, Width = 801, Height = 401 };

        var screen = CoordinateTransform.ToScreenPoint(
            new CoordinateTarget(17, 23, CoordinateSpace.Screen),
            window);
        TestAssert.Equal((17, 23), screen, "screen coordinates must remain unchanged");

        var local = CoordinateTransform.ToScreenPoint(
            new CoordinateTarget(12.5, 10.5, CoordinateSpace.Window),
            window);
        TestAssert.Equal((113, 211), local, "window coordinates must translate from the window origin");

        var normalized = CoordinateTransform.ToScreenPoint(
            new CoordinateTarget(0.5, 0.5, CoordinateSpace.Normalized),
            window);
        TestAssert.Equal((500, 400), normalized, "normalized coordinates must map to the window pixel extent");

        var bottomRight = CoordinateTransform.ToScreenPoint(
            new CoordinateTarget(1, 1, CoordinateSpace.Normalized),
            window);
        TestAssert.Equal((900, 600), bottomRight, "normalized one must map to the last window pixel");

        var legacy = TargetDescriptor.FromAction(new JsonObject
        {
            ["x"] = 17,
            ["y"] = 23
        });
        TestAssert.True(legacy.Coordinates is not null, "legacy x/y target must still parse");
        TestAssert.Equal(CoordinateSpace.Screen, legacy.Coordinates!.Value.Space, "legacy coordinates default to screen space");

        var nested = TargetDescriptor.FromAction(new JsonObject
        {
            ["coordinate_space"] = "window",
            ["target"] = new JsonObject
            {
                ["x"] = 12.5,
                ["y"] = 10.5
            }
        });
        TestAssert.Equal(CoordinateSpace.Window, nested.Coordinates!.Value.Space, "top-level coordinate space must apply to nested coordinates");

        var invalid = TestAssert.Throws<ComputerUseException>(
            () => CoordinateTransform.ToScreenPoint(
                new CoordinateTarget(1.1, 0.5, CoordinateSpace.Normalized),
                window),
            "out-of-range normalized coordinates must fail safely");
        TestAssert.Equal("INVALID_TARGET", invalid.Code, "invalid coordinate error code");
    }
}
