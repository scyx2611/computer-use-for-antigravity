using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal static class PostconditionEvaluator
{
    private static readonly HashSet<string> SupportedConditions = new(StringComparer.Ordinal)
    {
        "element",
        "element_exists",
        "element_absent",
        "element_enabled",
        "element_disabled",
        "value",
        "value_equals",
        "text",
        "text_equals",
        "text_contains",
        "window_title_contains",
        "title_contains",
        "url_equals",
        "url_contains",
        "ui_changed",
        "ui_stable",
        "page_changed",
        "page_stable",
        "navigation_complete"
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
                case "element_exists":
                case "element_absent":
                case "element_enabled":
                case "element_disabled":
                    ValidateElementTarget(entry.Value, entry.Key);
                    break;
                case "value":
                case "value_equals":
                    ValidateValueCondition(entry.Value, entry.Key);
                    break;
                case "text":
                case "text_equals":
                case "text_contains":
                    ValidateTextCondition(entry.Value, entry.Key);
                    break;
                case "window_title_contains":
                case "title_contains":
                case "url_equals":
                case "url_contains":
                    RequireNonEmptyString(entry.Value, entry.Key);
                    break;
                case "ui_changed":
                case "ui_stable":
                case "page_changed":
                case "page_stable":
                case "navigation_complete":
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
                case "element_exists":
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
                case "value_equals":
                {
                    var condition = RequireObject(entry.Value, entry.Key);
                    var element = ResolveElement(after, condition["target"]);
                    var expectedValue = RequireString(condition["equals"], $"{entry.Key}.equals", allowEmpty: true);
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
                case "text":
                case "text_equals":
                case "text_contains":
                {
                    checks.Add(EvaluateTextCondition(after, entry.Value, entry.Key));
                    break;
                }
                case "window_title_contains":
                case "title_contains":
                {
                    var expected = RequireString(entry.Value, entry.Key, allowEmpty: false);
                    var actual = after.Result.Browser?.Title ?? after.Result.Window.Title;
                    var passed = actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                    checks.Add(new PostconditionCheck
                    {
                        Type = entry.Key,
                        Passed = passed,
                        Expected = JsonValue.Create(expected),
                        Actual = JsonValue.Create(actual),
                        Message = passed
                            ? "The window title contains the expected text."
                            : $"Window title '{actual}' does not contain '{expected}'."
                    });
                    break;
                }
                case "url_equals":
                case "url_contains":
                {
                    var expected = RequireString(entry.Value, entry.Key, allowEmpty: false);
                    var actual = after.Result.Browser?.Url;
                    var passed = actual is not null
                        && (entry.Key == "url_equals"
                            ? string.Equals(actual, expected, StringComparison.Ordinal)
                            : actual.Contains(expected, StringComparison.Ordinal));
                    checks.Add(new PostconditionCheck
                    {
                        Type = entry.Key,
                        Passed = passed,
                        Expected = JsonValue.Create(expected),
                        Actual = actual is null ? null : JsonValue.Create(actual),
                        Message = actual is null
                            ? "The URL postcondition requires a managed browser observation."
                            : passed
                                ? $"The browser URL matched {entry.Key}."
                                : $"Browser URL '{actual}' did not satisfy {entry.Key} '{expected}'."
                    });
                    break;
                }
                case "ui_changed":
                case "ui_stable":
                case "page_changed":
                case "page_stable":
                case "navigation_complete":
                {
                    var expected = RequireBoolean(entry.Value, entry.Key);
                    var actual = entry.Key switch
                    {
                        "ui_changed" => !string.Equals(before.UiSignature, after.UiSignature, StringComparison.Ordinal),
                        "page_changed" => HasPageChanged(before, after),
                        "navigation_complete" => stability.NavigationComplete
                            ?? after.Result.Browser?.NavigationComplete
                            ?? false,
                        _ => stability.IsStable
                    };
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

        if (observation.ElementResolver is not null)
        {
            return observation.ElementResolver(target);
        }

        return TargetResolver.FindSnapshot(observation.Result.Elements, descriptor)?.Snapshot;
    }

    private static PostconditionCheck EvaluateTextCondition(
        WorkflowObservation after,
        JsonNode? node,
        string conditionName)
    {
        var condition = RequireObject(node, conditionName);
        var element = ResolveElement(after, condition["target"]);
        var expected = condition["contains"] is not null
            ? RequireString(condition["contains"], $"{conditionName}.contains", allowEmpty: true)
            : RequireString(condition["equals"], $"{conditionName}.equals", allowEmpty: true);
        var contains = condition["contains"] is not null;
        var actual = element is null ? null : ReadElementText(element);
        var passed = element is not null
            && actual is not null
            && (contains
                ? actual.Contains(expected, StringComparison.Ordinal)
                : string.Equals(actual, expected, StringComparison.Ordinal));

        return new PostconditionCheck
        {
            Type = contains ? "text_contains" : "text_equals",
            Passed = passed,
            Expected = JsonValue.Create(expected),
            Actual = actual is null ? null : JsonValue.Create(actual),
            Message = element is null
                ? "The text target was not found."
                : actual is null
                    ? "The text target has no readable text."
                    : passed
                        ? "The element text matched."
                        : $"Expected text '{expected}', actual '{actual}'."
        };
    }

    private static string ReadElementText(UiElementSnapshot element)
    {
        return element.Text
            ?? element.Value
            ?? element.Name;
    }

    private static bool HasPageChanged(WorkflowObservation before, WorkflowObservation after)
    {
        if (before.Result.Browser is { } beforeBrowser
            && after.Result.Browser is { } afterBrowser)
        {
            return !string.Equals(
                    beforeBrowser.TargetId,
                    afterBrowser.TargetId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    beforeBrowser.DocumentGeneration,
                    afterBrowser.DocumentGeneration,
                    StringComparison.Ordinal)
                || !string.Equals(beforeBrowser.Url, afterBrowser.Url, StringComparison.Ordinal)
                || !string.Equals(beforeBrowser.Title, afterBrowser.Title, StringComparison.Ordinal)
                || !string.Equals(before.UiSignature, after.UiSignature, StringComparison.Ordinal);
        }

        if (before.Result.Browser is not null || after.Result.Browser is not null)
        {
            return true;
        }

        return !string.Equals(before.UiSignature, after.UiSignature, StringComparison.Ordinal);
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
                $"{conditionName} must identify a UI element with element_id, name, role, automation_id, css, test_id, text, or placeholder.");
        }

        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "INVALID_POSTCONDITION",
                $"{conditionName} cannot use coordinate targets.");
        }
    }

    private static void ValidateValueCondition(JsonNode? node, string conditionName)
    {
        var condition = RequireObject(node, conditionName);
        if (condition["target"] is null)
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", $"{conditionName}.target is required.");
        }

        ValidateElementTarget(condition["target"], $"{conditionName}.target");
        _ = RequireString(condition["equals"], $"{conditionName}.equals", allowEmpty: true);
    }

    private static void ValidateTextCondition(JsonNode? node, string conditionName)
    {
        var condition = RequireObject(node, conditionName);
        if (condition["target"] is null)
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", $"{conditionName}.target is required.");
        }

        ValidateElementTarget(condition["target"], $"{conditionName}.target");
        var hasEquals = condition["equals"] is not null;
        var hasContains = condition["contains"] is not null;
        if (conditionName == "text_equals" && (!hasEquals || hasContains))
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", "text_equals requires equals and cannot specify contains.");
        }

        if (conditionName == "text_contains" && (!hasContains || hasEquals))
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", "text_contains requires contains and cannot specify equals.");
        }

        if (conditionName == "text" && hasEquals == hasContains)
        {
            throw new ComputerUseException("INVALID_POSTCONDITION", "text requires exactly one of equals or contains.");
        }

        if (hasEquals)
        {
            _ = RequireString(condition["equals"], $"{conditionName}.equals", allowEmpty: true);
        }

        if (hasContains)
        {
            _ = RequireString(condition["contains"], $"{conditionName}.contains", allowEmpty: true);
        }
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
