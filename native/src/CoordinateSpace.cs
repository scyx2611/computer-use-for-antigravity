using System.Globalization;

namespace ComputerUse.Native;

internal enum CoordinateSpace
{
    Screen,
    Window,
    Normalized
}

internal readonly record struct CoordinateTarget(double X, double Y, CoordinateSpace Space)
{
    public CoordinateTarget WithSpace(CoordinateSpace space) => this with { Space = space };

    public string Describe()
    {
        return $"({X.ToString("G", CultureInfo.InvariantCulture)},{Y.ToString("G", CultureInfo.InvariantCulture)}) [{CoordinateSpaceParser.ToWireName(Space)}]";
    }
}

internal static class CoordinateSpaceParser
{
    public static CoordinateSpace Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CoordinateSpace.Screen;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "screen" => CoordinateSpace.Screen,
            "window" => CoordinateSpace.Window,
            "normalized" => CoordinateSpace.Normalized,
            _ => throw new ComputerUseException(
                "INVALID_TARGET",
                "coordinate_space must be one of: screen, window, normalized.")
        };
    }

    public static string ToWireName(CoordinateSpace space)
    {
        return space switch
        {
            CoordinateSpace.Screen => "screen",
            CoordinateSpace.Window => "window",
            CoordinateSpace.Normalized => "normalized",
            _ => throw new ArgumentOutOfRangeException(nameof(space))
        };
    }
}

internal static class CoordinateTransform
{
    public static (int X, int Y) ToScreenPoint(CoordinateTarget target, WindowRectData window)
    {
        ValidateFinite(target.X, target.Y);

        return target.Space switch
        {
            CoordinateSpace.Screen => (ToInt32(target.X, "x"), ToInt32(target.Y, "y")),
            CoordinateSpace.Window =>
            (
                checked(window.X + ToInt32(target.X, "x")),
                checked(window.Y + ToInt32(target.Y, "y"))
            ),
            CoordinateSpace.Normalized => ToNormalizedScreenPoint(target, window),
            _ => throw new ArgumentOutOfRangeException(nameof(target.Space))
        };
    }

    private static (int X, int Y) ToNormalizedScreenPoint(CoordinateTarget target, WindowRectData window)
    {
        if (target.X is < 0 or > 1 || target.Y is < 0 or > 1)
        {
            throw new ComputerUseException(
                "INVALID_TARGET",
                "normalized coordinate targets require x and y between 0 and 1.");
        }

        if (window.Width <= 0 || window.Height <= 0)
        {
            throw new ComputerUseException(
                "INVALID_TARGET",
                "The target window has no drawable area for normalized coordinates.");
        }

        var x = window.X + ToInt32(target.X * Math.Max(0, window.Width - 1), "x");
        var y = window.Y + ToInt32(target.Y * Math.Max(0, window.Height - 1), "y");
        return (x, y);
    }

    private static void ValidateFinite(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ComputerUseException("INVALID_TARGET", "Coordinate targets must contain finite numbers.");
        }
    }

    private static int ToInt32(double value, string axis)
    {
        if (!double.IsFinite(value)
            || value < int.MinValue
            || value > int.MaxValue)
        {
            throw new ComputerUseException("INVALID_TARGET", $"Coordinate {axis} is outside the supported range.");
        }

        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }
}
