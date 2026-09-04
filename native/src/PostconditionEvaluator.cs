using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal static class PostconditionEvaluator
{
    private static readonly HashSet<string> SupportedConditions = new(StringComparer.Ordinal)
    {
        "element",
        "element_absent",
        "element_enabled",
        "element_disabled",
        "value",
        "window_title_contains",
        "ui_changed",
        "ui_stable"
    };

    public static void Validate(JsonNode? expectNode)
    {
        if (expectNode is null)
        {
            return;
        }

        if (expectNode is not JsonObject expect || expect.Count == 0)
        {
            throw new ComputerUseException(
                "INVALID_POSTCONDITION",
                "expect must be a non-empty JSON object.");
        }

        foreach (var entry in expect)
        {
            if (!SupportedConditions.Contains(entry.Key))
            {
                throw new ComputerUseException(
                    "INVALID_POSTCONDITION",
                    $"Unsupported postcondition '{entry.Key}'.");
            }

            switch (entry.Key)
            {
                case "element":
                case "element_absent":
                case "element_enabled":
                case "element_disabled":
                    ValidateElementTarget(entry.Value, entry.Key);
                    break;
                case "value":
                    ValidateValueCondition(entry.Value);
                    break;
                case "window_title_contains":
                    RequireNonEmptyString(entry.Value, "window_title_contains");
                    break;
                case "ui_changed":
                case "ui_stable":
                    RequireBoolean(entry.Value, entry.Key);
                    break;
            }
        }
    }

    public static PostconditionVerification Evaluate(
        JsonNode? expectNode,
        WorkflowObservation before,
        WorkflowObservation after,
        UiStabilityResult stability)
    {
        if (expectNode is null)
        {
            return PostconditionVerification.NotRequested();
        }

        Validate(expectNode);
        var expect = (JsonObject)expectNode;
        var checks = new List<PostconditionCheck>(expect.Count);

        foreach (var entry in expect)
        {
            switch (entry.Key)
            {
                case "element":
                {
                    var element = ResolveElement(after, entry.Value);
                    checks.Add(new PostconditionCheck
                    {
                        Type = "element_exists",
                        Passed = element is not null,
                        Expected = JsonValue.Create("exists"),
                        Actual = JsonValue.Create(element is null ? "absent" : Describe(element)),
                        Message = element is null
                            ? "Expected the element to exist, but it was not found."
                            : "The expected element exists."
                    });
                    break;
                }
                case "element_absent":
                {
                    var element = ResolveElement(after, entry.Value);
                    checks.Add(new PostconditionCheck
                    {
                        Type = "element_absent",
                        Passed = element is null,
                        Expected = JsonValue.Create("absent"),
                        Actual = JsonValue.Create(element is null ? "absent" : Describe(element)),
                        Message = element is null
                            ? "The element is absent as expected."
                            : "Expected the element to be absent, but it still exists."
                    });
                    break;
                }
                case "element_enabled":
                case "element_disabled":
                {
                    var element = ResolveElement(after, entry.Value);
                    var expected = entry.Key == "element_enabled";
                    var passed = element is not null && element.IsEnabled == expected;
                    checks.Add(new PostconditionCheck
                    {
                        Type = entry.Key,
                        Passed = passed,
                        Expected = JsonValue.Create(expected),
                        Actual = element is null ? null : JsonValue.Create(element.IsEnabled),
                        Message = element is null
                            ? "The expected element was not found."
                            : passed
                                ? $"The element enabled state is {expected}."
                                : $"Expected enabled={expected}, actual enabled={element.IsEnabled}."
                    });
                    break;
                }
                case "value":
                {
                    var condition = RequireObject(entry.Value, "value");
                    var element = ResolveElement(after, condition["target"]);
                    var expectedValue = RequireString(condition["equals"], "value.equals", allowEmpty: true);
                    var actualValue = element?.Value;
                    var passed = element is not null
                        && string.Equals(actualValue, expectedValue, StringComparison.Ordinal);
                    checks.Add(new PostconditionCheck
                    {
                        Type = "value_equals",
                        Passed = passed,
                        Expected = JsonValue.Create(expectedValue),
                        Actual = actualValue is null ? null : JsonValue.Create(actualValue),
                        Message = element is null
                            ? "The value target was not found."
                            : passed
                                ? "The element value matches."
                                : $"Expected value '{expectedValue}', actual '{actualValue ?? "<null>"}'."
                    });
                    break;
                }
                case "window_title_contains":
                {
                    var expected = RequireString(entry.Value, "window_title_contains", allowEmpty: false);
                    var actual = after.Result.Window.Title;
                    var passed = actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                    checks.Add(new PostconditionCheck
                    {
                        Type = "window_title_contains",
                        Passed = passed,
                        Expected = JsonValue.Create(expected),
                        Actual = JsonValue.Create(actual),
                        Message = passed
                            ? "The window title contains the expected text."
                            : $"Window title '{actual}' does not contain '{expected}'."
                    });
                    break;
                }
                case "ui_changed":
                case "ui_stable":
                {
                    var expected = RequireBoolean(entry.Value, entry.Key);
                    var actual = entry.Key == "ui_changed"
                        ? !string.Equals(before.UiSignature, after.UiSignature, StringComparison.Ordinal)
                        : stability.IsStable;
                    checks.Add(new PostconditionCheck
                    {
                        Type = entry.Key,
                        Passed = actual == expected,
                        Expected = JsonValue.Create(expected),
                        Actual = JsonValue.Create(actual),
                        Message = actual == expected
                            ? $"{entry.Key} matched."
                            : $"Expected {entry.Key}={expected}, actual {actual}."
                    });
                    break;
                }
            }
        }

        var failed = checks.Where(check => !check.Passed).Select(check => check.Type).ToArray();
        return failed.Length == 0
            ? PostconditionVerification.PassedResult(checks)
            : PostconditionVerification.FailedResult(
                "POSTCONDITION_FAILED",
                $"Postcondition failed: {string.Join(", ", failed)}.",
                checks);
    }

    private static UiElementSnapshot? ResolveElement(WorkflowObservation observation, JsonNode? targetNode)
    {
        var target = UnwrapTarget(targetNode);
        var descriptor = TargetDescriptor.FromAction(new JsonObject
        {
            ["target"] = target.DeepClone()
        });

        if (descriptor.ElementId is not null)
        {
            return observation.Result.Elements.FirstOrDefault(element => element.Id == descriptor.ElementId.Value);
        }

        return TargetResolver.FindSnapshot(observation.Result.Elements, descriptor)?.Snapshot;
    }

    private static void ValidateElementTarget(JsonNode? node, string conditionName)
    {
        var target = UnwrapTarget(node);
        var descriptor = TargetDescriptor.FromAction(new JsonObject
        {
            ["target"] = target.DeepClone()
        });
        if (descriptor.ElementId is null && !descriptor.HasSelector)
        {
            throw new ComputerUseException(
                "INVALID_POSTCONDITION",
                $"{conditionName} must identify a UI element with element_id, name, role, or automation_id.");
        }

        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "INVALID_POSTCONDITION",
                $"{conditionName} cannot use coordinate targets.");
        }
    }

    private static void ValidateValueCondition(JsonNode? node)
    {
        var condition = RequireObject(node, "value");
        if (condition["target"] is null)
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", "value.target is required.");
        }

        ValidateElementTarget(condition["target"], "value.target");
        _ = RequireString(condition["equals"], "value.equals", allowEmpty: true);
    }

    private static JsonNode UnwrapTarget(JsonNode? node)
    {
        if (node is JsonObject objectNode && objectNode["target"] is not null)
        {
            return objectNode["target"]!.DeepClone();
        }

        if (node is JsonObject or JsonValue)
        {
            return node!.DeepClone();
        }

        throw new ComputerUseException(
            "INVALID_POSTCONDITION",
            "A postcondition element target must be a selector object or string.");
    }

    private static JsonObject RequireObject(JsonNode? node, string name)
    {
        if (node is JsonObject objectNode)
        {
            return objectNode;
        }

        throw new ComputerUseException("INVALID_POSTCONDITION", $"{name} must be a JSON object.");
    }

    private static string RequireNonEmptyString(JsonNode? node, string name)
    {
        return RequireString(node, name, allowEmpty: false);
    }

    private static string RequireString(JsonNode? node, string name, bool allowEmpty)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            if (allowEmpty || !string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        throw new ComputerUseException("INVALID_POSTCONDITION", $"{name} must be a string.");
    }

    private static bool RequireBoolean(JsonNode? node, string name)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new ComputerUseException("INVALID_POSTCONDITION", $"{name} must be a boolean.");
    }

    private static string Describe(UiElementSnapshot element)
    {
        return $"{element.Role} '{element.Name}' (element_id={element.Id})";
    }
}
