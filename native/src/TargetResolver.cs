using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace ComputerUse.Native;

internal sealed class TargetDescriptor
{
    public int? ElementId { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public string? AutomationId { get; init; }

    public string? Css { get; init; }

    public string? TestId { get; init; }

    public string? Text { get; init; }

    public string? Placeholder { get; init; }

    public CoordinateTarget? Coordinates { get; init; }

    public bool HasSelector => Name is not null
        || Role is not null
        || AutomationId is not null
        || Css is not null
        || TestId is not null
        || Text is not null
        || Placeholder is not null;

    public static TargetDescriptor FromAction(JsonObject action)
    {
        var targetNode = action["target"];
        var actionSpace = ReadCoordinateSpace(action["coordinate_space"]) ?? CoordinateSpace.Screen;
        var descriptor = new TargetDescriptor
        {
            ElementId = ReadOptionalInt(action["element_id"]),
            Name = ReadOptionalString(action["name"]),
            Role = ReadOptionalString(action["role"]),
            AutomationId = ReadOptionalString(action["automation_id"]),
            Css = ReadOptionalString(action["css"]),
            TestId = ReadOptionalString(action["test_id"]),
            Placeholder = ReadOptionalString(action["placeholder"]),
            Coordinates = ReadCoordinates(action, actionSpace)
        };

        if (targetNode is JsonValue targetValue && targetValue.TryGetValue<string>(out var targetName))
        {
            return new TargetDescriptor
            {
                ElementId = descriptor.ElementId,
                Name = targetName,
                Role = descriptor.Role,
                AutomationId = descriptor.AutomationId,
                Css = descriptor.Css,
                TestId = descriptor.TestId,
                Text = descriptor.Text,
                Placeholder = descriptor.Placeholder,
                Coordinates = descriptor.Coordinates
            };
        }

        if (targetNode is JsonObject targetObject)
        {
            var targetSpace = ReadCoordinateSpace(targetObject["coordinate_space"]) ?? actionSpace;
            return new TargetDescriptor
            {
                ElementId = ReadOptionalInt(targetObject["element_id"] ?? targetObject["id"]) ?? descriptor.ElementId,
                Name = ReadOptionalString(targetObject["name"]) ?? descriptor.Name,
                Role = ReadOptionalString(targetObject["role"]) ?? descriptor.Role,
                AutomationId = ReadOptionalString(targetObject["automation_id"]) ?? descriptor.AutomationId,
                Css = ReadOptionalString(targetObject["css"]) ?? descriptor.Css,
                TestId = ReadOptionalString(targetObject["test_id"]) ?? descriptor.TestId,
                Text = ReadOptionalString(targetObject["text"]) ?? descriptor.Text,
                Placeholder = ReadOptionalString(targetObject["placeholder"]) ?? descriptor.Placeholder,
                Coordinates = ReadCoordinates(targetObject, targetSpace)
                    ?? descriptor.Coordinates?.WithSpace(targetSpace)
            };
        }

        return descriptor;
    }

    public string Describe()
    {
        if (ElementId is not null)
        {
            return $"element_id={ElementId.Value}";
        }

        if (Coordinates is { } point)
        {
            return point.Describe();
        }

        var parts = new List<string>();
        if (Name is not null)
        {
            parts.Add($"name='{Name}'");
        }

        if (Role is not null)
        {
            parts.Add($"role='{Role}'");
        }

        if (AutomationId is not null)
        {
            parts.Add($"automation_id='{AutomationId}'");
        }

        if (Css is not null)
        {
            parts.Add($"css='{Css}'");
        }

        if (TestId is not null)
        {
            parts.Add($"test_id='{TestId}'");
        }

        if (Text is not null)
        {
            parts.Add($"text='{Text}'");
        }

        if (Placeholder is not null)
        {
            parts.Add($"placeholder='{Placeholder}'");
        }

        return parts.Count == 0 ? "target" : string.Join(", ", parts);
    }

    private static string? ReadOptionalString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        throw new ComputerUseException("INVALID_TARGET", "Target text fields must be strings.");
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new ComputerUseException("INVALID_TARGET", "element_id must be an integer.");
    }

    private static CoordinateSpace? ReadCoordinateSpace(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return CoordinateSpaceParser.Parse(result);
        }

        throw new ComputerUseException("INVALID_TARGET", "coordinate_space must be a string.");
    }

    private static CoordinateTarget? ReadCoordinates(JsonObject objectNode, CoordinateSpace? space)
    {
        var xNode = objectNode["x"];
        var yNode = objectNode["y"];
        if (xNode is null && yNode is null)
        {
            return null;
        }

        if (TryReadNumber(xNode, out var x) && TryReadNumber(yNode, out var y))
        {
            return new CoordinateTarget(x, y, space ?? CoordinateSpace.Screen);
        }

        throw new ComputerUseException("INVALID_TARGET", "Coordinate targets require numeric x and y.");
    }

    private static bool TryReadNumber(JsonNode? node, out double result)
    {
        result = 0;
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        return false;
    }
}

internal sealed class TargetMatch
{
    public TargetMatch(UiElementSnapshot snapshot, int score, AutomationElementInfo? info = null)
    {
        Snapshot = snapshot;
        Score = score;
        Info = info;
    }

    public UiElementSnapshot Snapshot { get; }

    public int Score { get; }

    public AutomationElementInfo? Info { get; }
}

internal static class TargetResolver
{
    private const int AmbiguityScoreDelta = 50;

    public static AutomationElementInfo? FindBySelector(
        IReadOnlyList<AutomationElementInfo> candidates,
        TargetDescriptor descriptor)
    {
        var ranked = candidates
            .Select(candidate => new TargetMatch(candidate.Snapshot, Score(candidate.Snapshot, descriptor), candidate))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Snapshot.Id)
            .ToArray();

        EnsureUnambiguous(ranked, descriptor);
        return ranked.FirstOrDefault()?.Info;
    }

    internal static IReadOnlyList<TargetMatch> RankSnapshots(
        IReadOnlyList<UiElementSnapshot> candidates,
        TargetDescriptor descriptor)
    {
        return candidates
            .Select(candidate => new TargetMatch(candidate, Score(candidate, descriptor)))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Snapshot.Id)
            .ToArray();
    }

    internal static TargetMatch? FindSnapshot(
        IReadOnlyList<UiElementSnapshot> candidates,
        TargetDescriptor descriptor)
    {
        var ranked = RankSnapshots(candidates, descriptor);
        EnsureUnambiguous(ranked, descriptor);
        return ranked.FirstOrDefault();
    }

    internal static void EnsureUnambiguous(
        IReadOnlyList<TargetMatch> ranked,
        TargetDescriptor descriptor)
    {
        if (ranked.Count < 2)
        {
            return;
        }

        var bestScore = ranked[0].Score;
        var competitors = ranked
            .TakeWhile(match => bestScore - match.Score < AmbiguityScoreDelta)
            .ToArray();
        if (competitors.Length < 2)
        {
            return;
        }

        var candidateArray = new JsonArray();
        foreach (var candidate in competitors)
        {
            candidateArray.Add(new JsonObject
            {
                ["element_id"] = candidate.Snapshot.Id,
                ["name"] = candidate.Snapshot.Name,
                ["role"] = candidate.Snapshot.Role,
                ["automation_id"] = candidate.Snapshot.AutomationId,
                ["bounds"] = JsonSerializer.SerializeToNode(candidate.Snapshot.Bounds),
                ["is_enabled"] = candidate.Snapshot.IsEnabled,
                ["score"] = candidate.Score
            });
        }

        throw new ComputerUseException(
            "AMBIGUOUS_TARGET",
            $"The selector {descriptor.Describe()} matched multiple equally plausible UI elements; refine it with automation_id, role, name, or element_id.",
            new JsonObject
            {
                ["selector"] = descriptor.Describe(),
                ["candidates"] = candidateArray
            });
    }

    private static int Score(UiElementSnapshot candidate, TargetDescriptor descriptor)
    {
        var score = 0;

        if (descriptor.Role is not null)
        {
            if (string.Equals(candidate.Role, descriptor.Role, StringComparison.OrdinalIgnoreCase))
            {
                score += 250;
            }
            else
            {
                return -1;
            }
        }

        if (descriptor.AutomationId is not null)
        {
            if (string.Equals(candidate.AutomationId, descriptor.AutomationId, StringComparison.Ordinal))
            {
                score += 1_000;
            }
            else if (string.Equals(candidate.AutomationId, descriptor.AutomationId, StringComparison.OrdinalIgnoreCase))
            {
                score += 850;
            }
            else
            {
                return -1;
            }
        }

        if (descriptor.Name is not null)
        {
            if (string.Equals(candidate.Name, descriptor.Name, StringComparison.Ordinal))
            {
                score += 900;
            }
            else if (string.Equals(candidate.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase))
            {
                score += 800;
            }
            else if (candidate.Name.Contains(descriptor.Name, StringComparison.OrdinalIgnoreCase))
            {
                score += 650;
            }
            else if (descriptor.Name.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.Name))
            {
                score += 600;
            }
            else
            {
                var similarity = Similarity(candidate.Name, descriptor.Name);
                if (similarity < 0.45)
                {
                    return -1;
                }

                score += 400 + (int)(similarity * 150);
            }
        }

        return score;
    }

    private static double Similarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        left = Normalize(left);
        right = Normalize(right);
        if (left == right)
        {
            return 1;
        }

        var distance = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            distance[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var previousDiagonal = distance[0];
            distance[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var previous = distance[rightIndex];
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                distance[rightIndex] = Math.Min(
                    Math.Min(distance[rightIndex] + 1, distance[rightIndex - 1] + 1),
                    previousDiagonal + cost);
                previousDiagonal = previous;
            }
        }

        return 1.0 - (double)distance[^1] / Math.Max(left.Length, right.Length);
    }

    private static string Normalize(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
