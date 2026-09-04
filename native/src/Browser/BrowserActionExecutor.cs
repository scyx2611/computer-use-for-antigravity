using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class BrowserActionExecutor
{
    private readonly BrowserElementResolver resolver = new();
    private readonly CdpInputEngine keyboard = new();

    public ActionExecutionResult Execute(
        BrowserTargetContext targetContext,
        WindowState state,
        JsonObject action)
    {
        var browserState = state.Browser
            ?? throw new ComputerUseException(
                "STALE_BROWSER_STATE",
                "The requested state is not a managed browser observation.");
        var type = GetRequiredString(action, "type").ToLowerInvariant();
        var descriptor = TargetDescriptor.FromAction(action);

        switch (type)
        {
            case "click":
                return Click(targetContext, browserState, descriptor, action);
            case "type_text":
                return TypeText(targetContext, browserState, descriptor, action);
            case "set_value":
                return SetValue(targetContext, browserState, descriptor, action);
            case "press_key":
            case "hotkey":
                return PressKey(targetContext, browserState, descriptor, action);
            case "scroll":
                return Scroll(targetContext, browserState, descriptor, action);
            case "navigate":
                return Navigate(targetContext, action);
            case "double_click":
            case "dblclick":
            case "right_click":
            case "context_click":
            case "drag":
            case "wait":
                throw UnsupportedBrowserAction(type);
            default:
                throw new ComputerUseException("UNSUPPORTED_ACTION", $"Unsupported action type '{type}'.");
        }
    }

    private ActionExecutionResult Click(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        JsonObject action)
    {
        var target = ResolveRequired(targetContext, state, descriptor, allowFocusedEditable: false, "click");
        var point = ScrollAndGetCenter(targetContext.Connection, target.Element!);
        EnsureEnabled(target.Element!);
        targetContext.Connection.DispatchMouseEvent("mouseMoved", point.X, point.Y);
        targetContext.Connection.DispatchMouseEvent("mousePressed", point.X, point.Y, "left", buttons: 1, clickCount: 1);
        targetContext.Connection.DispatchMouseEvent("mouseReleased", point.X, point.Y, "left", buttons: 0, clickCount: 1);
        AddPoint(target.Resolution, point);
        return Succeeded("click", target, "Browser click completed.");
    }

    private ActionExecutionResult TypeText(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        JsonObject action)
    {
        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "UNSUPPORTED_BROWSER_TARGET",
                "Managed browser text input requires a semantic target; coordinates are not used as a fallback.");
        }

        var target = resolver.Resolve(targetContext, state, descriptor, allowFocusedEditable: true)
            ?? throw new ComputerUseException("TARGET_REQUIRED", "type_text requires a target or focused editable element.");
        EnsureEditable(target.Element!, "type_text");
        Focus(targetContext.Connection, target.Element!);
        var text = GetRequiredString(action, "text");
        targetContext.Connection.InsertText(text);
        return Succeeded("type_text", target, "Browser text input completed.");
    }

    private ActionExecutionResult SetValue(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        JsonObject action)
    {
        var target = ResolveRequired(targetContext, state, descriptor, allowFocusedEditable: false, "set_value");
        EnsureEditable(target.Element!, "set_value");
        Focus(targetContext.Connection, target.Element!);
        var value = GetString(action, "value", allowEmpty: true);
        keyboard.Press(targetContext.Connection, "A", ["CTRL"]);
        keyboard.Press(targetContext.Connection, "BACKSPACE");
        if (value.Length > 0)
        {
            targetContext.Connection.InsertText(value);
        }

        return Succeeded("set_value", target, "Browser value set.");
    }

    private ActionExecutionResult PressKey(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        JsonObject action)
    {
        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "UNSUPPORTED_BROWSER_TARGET",
                "Managed browser key actions require a semantic target when a target is supplied.");
        }

        BrowserResolvedTarget? target = null;
        if (descriptor.HasSelector)
        {
            target = ResolveRequired(targetContext, state, descriptor, allowFocusedEditable: false, "press_key");
            Focus(targetContext.Connection, target.Element!);
        }

        keyboard.Press(targetContext.Connection, GetRequiredString(action, "key"), ReadModifiers(action));
        return Succeeded("press_key".Equals(GetRequiredString(action, "type"), StringComparison.OrdinalIgnoreCase)
            ? "press_key"
            : "hotkey", target, "Browser key press completed.");
    }

    private ActionExecutionResult Scroll(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        JsonObject action)
    {
        var amount = GetInt(action, "amount", 1, -100, 100);
        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "UNSUPPORTED_BROWSER_TARGET",
                "Managed browser scrolling requires a semantic target or the viewport center; coordinates are not used as a fallback.");
        }

        var target = descriptor.HasSelector
            ? ResolveRequired(targetContext, state, descriptor, allowFocusedEditable: false, "scroll")
            : null;
        var point = target is not null
            ? ScrollAndGetCenter(targetContext.Connection, target.Element!)
            : ReadViewportCenter(targetContext);

        targetContext.Connection.DispatchMouseEvent(
            "mouseWheel",
            point.X,
            point.Y,
            deltaY: amount * 100);
        var resolution = target is not null
            ? target.Resolution
            : new JsonObject
            {
                ["method"] = "viewport_center",
                ["coordinate_space"] = "viewport",
                ["x"] = point.X,
                ["y"] = point.Y
            };
        return new ActionExecutionResult(
            new ActionRecord
            {
                Type = "scroll",
                Success = true,
                Target = descriptor.HasSelector ? descriptor.Describe() : "viewport center",
                Message = "Browser scroll completed."
            },
            resolution,
            true);
    }

    private ActionExecutionResult Navigate(BrowserTargetContext targetContext, JsonObject action)
    {
        var url = GetRequiredString(action, "url");
        BrowserUrlPolicy.Validate(url);
        var response = targetContext.Connection.Navigate(url);
        var errorText = ReadString(response, "errorText");
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            throw new ComputerUseException(
                "BROWSER_NAVIGATION_FAILED",
                $"The managed browser could not navigate to '{url}': {errorText}",
                new JsonObject
                {
                    ["url"] = url,
                    ["error_text"] = errorText
                });
        }

        targetContext.Session.MarkNavigation(url, ReadString(response, "loaderId"));

        return new ActionExecutionResult(
            new ActionRecord
            {
                Type = "navigate",
                Success = true,
                Target = url,
                Message = "Browser navigation started."
            },
            new JsonObject
            {
                ["method"] = "navigate",
                ["url"] = url,
                ["loader_id"] = ReadString(response, "loaderId"),
                ["frame_id"] = ReadString(response, "frameId")
            },
            true);
    }

    private BrowserResolvedTarget ResolveRequired(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        bool allowFocusedEditable,
        string actionType)
    {
        if (descriptor.Coordinates is not null)
        {
            throw new ComputerUseException(
                "UNSUPPORTED_BROWSER_TARGET",
                $"Managed browser {actionType} requires a semantic target; explicit coordinates are not used as a browser fallback.");
        }

        return resolver.Resolve(targetContext, state, descriptor, allowFocusedEditable)
            ?? throw new ComputerUseException("TARGET_REQUIRED", $"{actionType} requires a browser element target.");
    }

    private static void EnsureEnabled(BrowserElementHandle element)
    {
        if (!element.Snapshot.IsEnabled)
        {
            throw new ComputerUseException(
                "ELEMENT_DISABLED",
                $"Browser element {element.ElementId} is disabled.");
        }
    }

    private static void EnsureEditable(BrowserElementHandle element, string actionType)
    {
        if (!element.IsEditable)
        {
            throw new ComputerUseException(
                "ELEMENT_NOT_EDITABLE",
                $"Browser {actionType} requires an editable element.");
        }
    }

    private static void Focus(CdpConnection connection, BrowserElementHandle element)
    {
        EnsureEnabled(element);
        _ = connection.Focus(element.BackendNodeId);
    }

    private static (double X, double Y) ScrollAndGetCenter(
        CdpConnection connection,
        BrowserElementHandle element)
    {
        _ = connection.ScrollIntoViewIfNeeded(element.BackendNodeId);
        var box = connection.GetBoxModel(element.BackendNodeId);
        var bounds = ReadBoxBounds(box);
        if (bounds is null)
        {
            throw new ComputerUseException(
                "BROWSER_ELEMENT_NOT_INTERACTABLE",
                $"Browser element {element.ElementId} has no visible box model.");
        }

        return (bounds.Value.Left + bounds.Value.Width / 2.0, bounds.Value.Top + bounds.Value.Height / 2.0);
    }

    private static (double X, double Y) ReadViewportCenter(BrowserTargetContext targetContext)
    {
        var document = targetContext.Connection.GetLayoutMetrics();
        var viewport = document["cssVisualViewport"] as JsonObject
            ?? document["visualViewport"] as JsonObject;
        var width = ReadDouble(viewport?[
            "clientWidth"]) ?? 1;
        var height = ReadDouble(viewport?["clientHeight"]) ?? 1;
        return (Math.Max(1, width / 2), Math.Max(1, height / 2));
    }

    private static void AddPoint(JsonObject? resolution, (double X, double Y) point)
    {
        if (resolution is null)
        {
            return;
        }

        resolution["point"] = new JsonArray { point.X, point.Y };
    }

    private static ActionExecutionResult Succeeded(
        string type,
        BrowserResolvedTarget? target,
        string message)
    {
        return new ActionExecutionResult(
            new ActionRecord
            {
                Type = type,
                Success = true,
                Target = target?.Label,
                Message = message
            },
            target?.Resolution,
            true);
    }

    private static ComputerUseException UnsupportedBrowserAction(string type)
    {
        return new ComputerUseException(
            "UNSUPPORTED_BROWSER_ACTION",
            $"Managed browser action '{type}' is not supported in v0.4 Phase 2.",
            new JsonObject
            {
                ["surface"] = "browser",
                ["backend"] = "browser_cdp",
                ["action_type"] = type
            });
    }

    private static string GetRequiredString(JsonObject objectNode, string name)
    {
        return GetString(objectNode, name, allowEmpty: false);
    }

    private static string GetString(JsonObject objectNode, string name, bool allowEmpty)
    {
        if (objectNode[name] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && (allowEmpty || !string.IsNullOrWhiteSpace(result)))
        {
            return result;
        }

        throw new ComputerUseException(
            "INVALID_ACTION",
            $"{name} is required and must be a {(allowEmpty ? "string" : "non-empty string") }.");
    }

    private static int GetInt(JsonObject objectNode, string name, int defaultValue, int minimum, int maximum)
    {
        if (objectNode[name] is null)
        {
            return defaultValue;
        }

        if (objectNode[name] is not JsonValue value || !value.TryGetValue<int>(out var result))
        {
            throw new ComputerUseException("INVALID_ACTION", $"{name} must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new ComputerUseException("INVALID_ACTION", $"{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static IReadOnlyList<string>? ReadModifiers(JsonObject action)
    {
        if (action["modifiers"] is not JsonArray values)
        {
            return null;
        }

        var modifiers = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var modifier))
            {
                throw new ComputerUseException("INVALID_ACTION", "modifiers must contain only strings.");
            }

            modifiers.Add(modifier);
        }

        return modifiers;
    }

    private static string? ReadString(JsonObject node, string propertyName)
    {
        return node[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var result))
        {
            return result;
        }

        return value.TryGetValue<int>(out var integer) ? integer : null;
    }

    private static (double Left, double Top, double Width, double Height)? ReadBoxBounds(JsonObject response)
    {
        if (response["model"] is not JsonObject model)
        {
            return null;
        }

        var quad = model["border"] as JsonArray ?? model["content"] as JsonArray;
        if (quad is null || quad.Count < 8)
        {
            return null;
        }

        var points = quad.Select(ReadDouble).ToArray();
        if (points.Any(point => point is null))
        {
            return null;
        }

        var x = points.Where((_, index) => index % 2 == 0).Select(point => point!.Value).ToArray();
        var y = points.Where((_, index) => index % 2 == 1).Select(point => point!.Value).ToArray();
        var left = x.Min();
        var top = y.Min();
        var width = Math.Max(0, x.Max() - left);
        var height = Math.Max(0, y.Max() - top);
        return width > 0 && height > 0 ? (left, top, width, height) : null;
    }
}
