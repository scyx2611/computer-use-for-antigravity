using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace ComputerUse.Native;

internal sealed class ActionExecutor
{
    private readonly AutomationEngine automation;
    private readonly InputEngine input;
    private readonly StateManager states;
    private readonly WindowManager windows;

    public ActionExecutor(
        AutomationEngine automation,
        InputEngine input,
        StateManager states,
        WindowManager windows)
    {
        this.automation = automation;
        this.input = input;
        this.states = states;
        this.windows = windows;
    }

    public ActionRecord Execute(IntPtr hwnd, WindowState state, JsonObject action)
    {
        var type = GetRequiredString(action, "type").ToLowerInvariant();
        var target = ResolveTarget(hwnd, state, action, allowFocusedTextTarget: type == "type_text");

        switch (type)
        {
            case "click":
                RequireTarget(target, type);
                if (target.Coordinates is not null)
                {
                    input.ClickAt(ResolvePoint(target, hwnd), windows, hwnd, rightButton: false, doubleClick: false);
                }
                else
                {
                    input.Click(target.Element!, target.Snapshot!, windows, hwnd);
                }

                return Succeeded(type, target, "Click completed.");

            case "double_click":
            case "dblclick":
                RequireTarget(target, type);
                if (target.Coordinates is not null)
                {
                    input.ClickAt(ResolvePoint(target, hwnd), windows, hwnd, rightButton: false, doubleClick: true);
                }
                else
                {
                    input.DoubleClick(target.Element!, target.Snapshot!, windows, hwnd);
                }

                return Succeeded(type, target, "Double-click completed.");

            case "right_click":
            case "context_click":
                RequireTarget(target, type);
                if (target.Coordinates is not null)
                {
                    input.ClickAt(ResolvePoint(target, hwnd), windows, hwnd, rightButton: true, doubleClick: false);
                }
                else
                {
                    input.RightClick(target.Element!, target.Snapshot!, windows, hwnd);
                }

                return Succeeded(type, target, "Right-click completed.");

            case "type_text":
                var text = GetRequiredString(action, "text");
                input.TypeText(target.Element, target.Snapshot, text, windows, hwnd);
                return Succeeded(type, target, "Text input completed.");

            case "set_value":
                RequireElementTarget(target, type);
                input.SetValue(
                    target.Element!,
                    target.Snapshot!,
                    GetRequiredString(action, "value"),
                    windows,
                    hwnd);
                return Succeeded(type, target, "Value set.");

            case "press_key":
            case "hotkey":
                windows.ActivateWindow(hwnd);
                input.PressKey(GetRequiredString(action, "key"), ReadModifiers(action));
                return Succeeded(type, target, "Key press completed.");

            case "scroll":
                var scrollPoint = ResolvePoint(target, hwnd);
                input.Scroll(GetInt(action, "amount", 1, -100, 100), scrollPoint.X, scrollPoint.Y, windows, hwnd);
                return Succeeded(type, target, "Scroll completed.");

            case "drag":
                var startTarget = ResolveSecondaryTarget(hwnd, state, action, "start_target", "start");
                var endTarget = ResolveSecondaryTarget(hwnd, state, action, "end_target", "end");
                input.Drag(
                    ResolvePoint(startTarget, hwnd),
                    ResolvePoint(endTarget, hwnd),
                    windows,
                    hwnd);
                return new ActionRecord
                {
                    Type = type,
                    Success = true,
                    Target = $"{startTarget.Label} -> {endTarget.Label}",
                    Message = "Drag completed."
                };

            case "wait":
                var milliseconds = GetInt(action, "milliseconds", 100, 0, 30_000);
                Thread.Sleep(milliseconds);
                return Succeeded(type, target, $"Waited {milliseconds} ms.");

            default:
                throw new ComputerUseException("UNSUPPORTED_ACTION", $"Unsupported action type '{type}'.");
        }
    }

    private ResolvedTarget ResolveTarget(
        IntPtr hwnd,
        WindowState state,
        JsonObject action,
        bool allowFocusedTextTarget)
    {
        var descriptor = TargetDescriptor.FromAction(action);
        if (descriptor.ElementId is not null)
        {
            var expected = states.RequireElement(state, descriptor.ElementId.Value);
            var currentElements = automation.GetElementInfos(hwnd);
            var current = FindCurrentElement(currentElements, expected);
            if (current is null)
            {
                throw new ComputerUseException(
                    "STALE_STATE",
                    "The requested UI element is no longer available; observe again.",
                    new JsonObject
                    {
                        ["state_id"] = state.StateId,
                        ["element_id"] = expected.Id
                    });
            }

            StateManager.ValidateElementDrift(expected, current.Snapshot);
            return new ResolvedTarget(current.Element, current.Snapshot, null, Describe(expected));
        }

        if (descriptor.HasSelector)
        {
            var currentElements = automation.GetElementInfos(hwnd);
            var current = TargetResolver.FindBySelector(currentElements, descriptor);
            if (current is null)
            {
                throw new ComputerUseException(
                    "TARGET_NOT_FOUND",
                    $"No UI element matched {descriptor.Describe()}.");
            }

            return new ResolvedTarget(current.Element, current.Snapshot, null, Describe(current.Snapshot));
        }

        if (descriptor.Coordinates is not null)
        {
            return new ResolvedTarget(null, null, descriptor.Coordinates, descriptor.Describe());
        }

        if (allowFocusedTextTarget)
        {
            var focused = automation.GetFocusedElement();
            if (focused is not null && automation.IsElementInWindow(focused, hwnd))
            {
                return new ResolvedTarget(focused, null, null, "focused element");
            }

            if (focused is not null)
            {
                throw new ComputerUseException(
                    "FOCUSED_ELEMENT_NOT_IN_WINDOW",
                    "The focused UI element belongs to a different window; provide an explicit target.");
            }
        }

        return new ResolvedTarget(null, null, null, null);
    }

    private ResolvedTarget ResolveSecondaryTarget(
        IntPtr hwnd,
        WindowState state,
        JsonObject action,
        string objectName,
        string fallbackName)
    {
        var targetNode = action[objectName] ?? action[fallbackName];
        if (targetNode is null)
        {
            throw new ComputerUseException("TARGET_REQUIRED", $"{objectName} or {fallbackName} is required for drag.");
        }

        var targetAction = new JsonObject
        {
            ["target"] = targetNode.DeepClone()
        };
        if (action["coordinate_space"] is not null)
        {
            targetAction["coordinate_space"] = action["coordinate_space"]!.DeepClone();
        }
        var descriptor = TargetDescriptor.FromAction(targetAction);
        if (descriptor.ElementId is not null)
        {
            var expected = states.RequireElement(state, descriptor.ElementId.Value);
            var currentElements = automation.GetElementInfos(hwnd);
            var current = FindCurrentElement(currentElements, expected);
            if (current is null)
            {
                throw new ComputerUseException("STALE_STATE", "A drag target is no longer available; observe again.");
            }

            StateManager.ValidateElementDrift(expected, current.Snapshot);
            return new ResolvedTarget(current.Element, current.Snapshot, null, Describe(expected));
        }

        if (descriptor.HasSelector)
        {
            var current = TargetResolver.FindBySelector(automation.GetElementInfos(hwnd), descriptor);
            if (current is null)
            {
                throw new ComputerUseException("TARGET_NOT_FOUND", $"No drag target matched {descriptor.Describe()}.");
            }

            return new ResolvedTarget(current.Element, current.Snapshot, null, Describe(current.Snapshot));
        }

        if (descriptor.Coordinates is not null)
        {
            return new ResolvedTarget(null, null, descriptor.Coordinates, descriptor.Describe());
        }

        throw new ComputerUseException("TARGET_REQUIRED", $"{objectName} must identify an element or coordinate.");
    }

    private static AutomationElementInfo? FindCurrentElement(
        IReadOnlyList<AutomationElementInfo> currentElements,
        UiElementSnapshot expected)
    {
        if (!string.IsNullOrWhiteSpace(expected.RuntimeId))
        {
            var byRuntimeId = currentElements.FirstOrDefault(candidate =>
                string.Equals(candidate.Snapshot.RuntimeId, expected.RuntimeId, StringComparison.Ordinal));
            if (byRuntimeId is not null)
            {
                return byRuntimeId;
            }
        }

        return currentElements.FirstOrDefault(candidate =>
            string.Equals(candidate.Snapshot.Role, expected.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Snapshot.Name, expected.Name, StringComparison.Ordinal)
            && string.Equals(candidate.Snapshot.AutomationId, expected.AutomationId, StringComparison.Ordinal));
    }

    private static void RequireTarget(ResolvedTarget target, string actionType)
    {
        if (target.Element is null && target.Coordinates is null)
        {
            throw new ComputerUseException("TARGET_REQUIRED", $"{actionType} requires target or coordinates.");
        }
    }

    private static void RequireElementTarget(ResolvedTarget target, string actionType)
    {
        if (target.Element is null || target.Snapshot is null)
        {
            throw new ComputerUseException("ELEMENT_TARGET_REQUIRED", $"{actionType} requires a UI element target.");
        }
    }

    private (int X, int Y) ResolvePoint(ResolvedTarget target, IntPtr hwnd)
    {
        if (target.Coordinates is { } point)
        {
            return CoordinateTransform.ToScreenPoint(point, windows.GetWindowRectData(hwnd));
        }

        if (target.Snapshot is not null)
        {
            var bounds = target.Snapshot.Bounds;
            if (bounds.Length >= 4)
            {
                return (bounds[0] + Math.Max(0, bounds[2] / 2), bounds[1] + Math.Max(0, bounds[3] / 2));
            }
        }

        throw new ComputerUseException("TARGET_REQUIRED", "An element or coordinate target is required.");
    }

    private static ActionRecord Succeeded(string type, ResolvedTarget target, string message)
    {
        return new ActionRecord
        {
            Type = type,
            Success = true,
            Target = target.Label,
            Message = message
        };
    }

    private static string Describe(UiElementSnapshot element)
    {
        return $"{element.Role} '{element.Name}'";
    }

    private static string GetRequiredString(JsonObject objectNode, string name)
    {
        var node = objectNode[name];
        if (node is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new ComputerUseException("INVALID_ACTION", $"{name} is required and must be a non-empty string.");
    }

    private static int GetInt(JsonObject objectNode, string name, int defaultValue, int minimum, int maximum)
    {
        var node = objectNode[name];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var result))
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
        if (action["modifiers"] is not JsonArray modifierArray)
        {
            return null;
        }

        var modifiers = new List<string>(modifierArray.Count);
        foreach (var modifier in modifierArray)
        {
            if (modifier is not JsonValue value || !value.TryGetValue<string>(out var name))
            {
                throw new ComputerUseException("INVALID_ACTION", "modifiers must contain only strings.");
            }

            modifiers.Add(name);
        }

        return modifiers;
    }

    private sealed class ResolvedTarget
    {
        public ResolvedTarget(
            AutomationElement? element,
            UiElementSnapshot? snapshot,
            CoordinateTarget? coordinates,
            string? label)
        {
            Element = element;
            Snapshot = snapshot;
            Coordinates = coordinates;
            Label = label;
        }

        public AutomationElement? Element { get; }

        public UiElementSnapshot? Snapshot { get; }

        public CoordinateTarget? Coordinates { get; }

        public string? Label { get; }
    }

}
