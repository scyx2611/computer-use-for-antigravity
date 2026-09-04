using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class StateManager
{
    private const long StateTtlMilliseconds = 5_000;
    private readonly Dictionary<string, WindowState> states = new(StringComparer.Ordinal);
    private readonly long stateTtlMilliseconds;
    private readonly Func<long> nowMilliseconds;

    public StateManager()
        : this(StateTtlMilliseconds, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    {
    }

    internal StateManager(long stateTtlMilliseconds, Func<long> nowMilliseconds)
    {
        if (stateTtlMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateTtlMilliseconds));
        }

        this.stateTtlMilliseconds = stateTtlMilliseconds;
        this.nowMilliseconds = nowMilliseconds ?? throw new ArgumentNullException(nameof(nowMilliseconds));
    }

    public WindowState Create(
        IntPtr hwnd,
        WindowRectData windowRect,
        string windowTitle,
        string? screenshotHash,
        IReadOnlyList<UiElementSnapshot> elements,
        BrowserStateMetadata? browser = null)
    {
        RemoveExpired();

        var state = new WindowState
        {
            StateId = Guid.NewGuid().ToString("N"),
            Hwnd = hwnd,
            CreatedUnixMilliseconds = nowMilliseconds(),
            WindowRect = windowRect,
            WindowTitle = windowTitle,
            ScreenshotHash = screenshotHash,
            Elements = elements,
            Browser = browser
        };
        states[state.StateId] = state;
        return state;
    }

    public WindowState Require(string? stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId))
        {
            throw new ComputerUseException("STATE_REQUIRED", "state_id is required for this action.");
        }

        RemoveExpired();

        if (!states.TryGetValue(stateId, out var state))
        {
            throw new ComputerUseException(
                "STALE_STATE",
                "The UI state is missing or older than five seconds; observe again.",
                new JsonObject { ["state_id"] = stateId });
        }

        return state;
    }

    public UiElementSnapshot RequireElement(WindowState state, int elementId)
    {
        var element = state.Elements.FirstOrDefault(candidate => candidate.Id == elementId);
        if (element is null)
        {
            throw new ComputerUseException(
                "ELEMENT_NOT_FOUND",
                $"Element id {elementId} is not present in state '{state.StateId}'. Observe again.",
                new JsonObject
                {
                    ["state_id"] = state.StateId,
                    ["element_id"] = elementId
                });
        }

        return element;
    }

    public void ValidateWindow(WindowState state, IntPtr hwnd, WindowManager windows)
    {
        if (state.Hwnd != hwnd)
        {
            throw new ComputerUseException(
                "STATE_WINDOW_MISMATCH",
                "The state belongs to a different window; observe the requested window again.",
                new JsonObject
                {
                    ["state_id"] = state.StateId,
                    ["expected_window_id"] = WindowManager.FormatWindowId(state.Hwnd),
                    ["actual_window_id"] = WindowManager.FormatWindowId(hwnd)
                });
        }

        var currentRect = windows.GetWindowRectData(hwnd);
        if (!state.WindowRect.EqualsTo(currentRect))
        {
            throw new ComputerUseException(
                "STALE_STATE",
                "The window moved or resized; observe again before acting.",
                new JsonObject
                {
                    ["state_id"] = state.StateId,
                    ["expected_rect"] = JsonSerializerHelper.ToNode(state.WindowRect),
                    ["actual_rect"] = JsonSerializerHelper.ToNode(currentRect)
                });
        }
    }

    public static void ValidateElementDrift(UiElementSnapshot expected, UiElementSnapshot actual)
    {
        var sameRuntimeId = !string.IsNullOrWhiteSpace(expected.RuntimeId)
            && string.Equals(expected.RuntimeId, actual.RuntimeId, StringComparison.Ordinal);
        var sameIdentity = string.Equals(expected.Role, actual.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
            && string.Equals(expected.AutomationId, actual.AutomationId, StringComparison.Ordinal);

        if (!sameRuntimeId && !sameIdentity)
        {
            throw new ComputerUseException(
                "STALE_STATE",
                "The requested UI element changed; observe again.");
        }

        if (HasSubstantialBoundsChange(expected.Bounds, actual.Bounds))
        {
            throw new ComputerUseException(
                "STALE_STATE",
                "The requested UI element moved or changed size; observe again.");
        }
    }

    private void RemoveExpired()
    {
        var now = nowMilliseconds();
        var expired = states
            .Where(pair => now - pair.Value.CreatedUnixMilliseconds > stateTtlMilliseconds)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var stateId in expired)
        {
            states.Remove(stateId);
        }
    }

    private static bool HasSubstantialBoundsChange(int[] expected, int[] actual)
    {
        if (expected.Length < 4 || actual.Length < 4)
        {
            return true;
        }

        var positionMoved = Math.Abs(expected[0] - actual[0]) > 64
            || Math.Abs(expected[1] - actual[1]) > 64;
        var widthChanged = Math.Abs(expected[2] - actual[2]) > Math.Max(32, expected[2] / 4);
        var heightChanged = Math.Abs(expected[3] - actual[3]) > Math.Max(32, expected[3] / 4);
        return positionMoved || widthChanged || heightChanged;
    }
}

internal static class JsonSerializerHelper
{
    public static JsonNode ToNode<T>(T value)
    {
        return System.Text.Json.JsonSerializer.SerializeToNode(value) ?? new JsonObject();
    }
}
