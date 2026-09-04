using System.Text.Json.Serialization;

namespace ComputerUse.Native;

internal sealed class WindowInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public uint Pid { get; init; }
}

internal sealed class WindowRectData
{
    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    public int[] ToBounds() => [X, Y, Width, Height];

    public bool EqualsTo(WindowRectData? other)
    {
        return other is not null
            && X == other.X
            && Y == other.Y
            && Width == other.Width
            && Height == other.Height;
    }
}

internal sealed class WindowSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("pid")]
    public uint Pid { get; init; }
}

internal sealed class UiElementSnapshot
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("automation_id")]
    public string AutomationId { get; init; } = string.Empty;

    /// <summary>
    /// Screen-coordinate bounds: [left, top, width, height].
    /// </summary>
    [JsonPropertyName("bounds")]
    public int[] Bounds { get; init; } = [0, 0, 0, 0];

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; }

    [JsonPropertyName("is_offscreen")]
    public bool IsOffscreen { get; init; }

    [JsonPropertyName("runtime_id")]
    public string RuntimeId { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }

    [JsonPropertyName("checked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Checked { get; init; }

    [JsonPropertyName("selected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Selected { get; init; }

    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; init; }

    [JsonPropertyName("test_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestId { get; init; }

    [JsonPropertyName("is_focused")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFocused { get; init; }
}

internal sealed class ObserveResult
{
    [JsonPropertyName("state_id")]
    public string StateId { get; init; } = string.Empty;

    [JsonPropertyName("window")]
    public WindowSnapshot Window { get; init; } = new();

    [JsonPropertyName("interaction")]
    public InteractionInfo Interaction { get; init; } = new();

    [JsonPropertyName("browser")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserInfo? Browser { get; init; }

    [JsonPropertyName("elements")]
    public IReadOnlyList<UiElementSnapshot> Elements { get; init; } = [];

    [JsonPropertyName("screenshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Screenshot { get; init; }

    [JsonPropertyName("screenshot_hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotHash { get; init; }

    [JsonPropertyName("screenshot_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotError { get; init; }

    [JsonPropertyName("capture")]
    public CaptureDiagnostics Capture { get; init; } = new();

    [JsonPropertyName("coordinate_spaces")]
    public CoordinateSpaces CoordinateSpaces { get; init; } = new();

    [JsonPropertyName("uia_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UiaError { get; init; }
}

internal sealed class ScreenshotResult
{
    public string? Base64 { get; init; }

    public string? Hash { get; init; }

    public string? Error { get; init; }

    public CaptureDiagnostics Diagnostics { get; init; } = new();
}

internal sealed class CaptureDiagnostics
{
    [JsonPropertyName("backend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Backend { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; init; }

    [JsonPropertyName("fallback_used")]
    public bool FallbackUsed { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<CaptureFailure> Errors { get; init; } = [];
}

internal sealed class CaptureFailure
{
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

internal sealed class ActionRecord
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed class PerformResult
{
    [JsonPropertyName("window")]
    public WindowSnapshot Window { get; init; } = new();

    [JsonPropertyName("interaction")]
    public InteractionInfo Interaction { get; init; } = new();

    [JsonPropertyName("browser")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserInfo? Browser { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<ActionRecord> Actions { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = "succeeded";

    [JsonPropertyName("execution_trace")]
    public IReadOnlyList<ExecutionTraceEntry> ExecutionTrace { get; init; } = [];

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("state_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateId { get; init; }

    [JsonPropertyName("screenshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Screenshot { get; init; }

    [JsonPropertyName("screenshot_hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotHash { get; init; }

    [JsonPropertyName("screenshot_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotError { get; init; }

    [JsonPropertyName("capture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureDiagnostics? Capture { get; init; }

    [JsonPropertyName("coordinate_spaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoordinateSpaces? CoordinateSpaces { get; init; }

    [JsonPropertyName("elements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UiElementSnapshot>? Elements { get; init; }

    [JsonPropertyName("uia_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UiaError { get; init; }
}

internal sealed class CoordinateSpaces
{
    [JsonPropertyName("screenshot")]
    public string Screenshot { get; init; } = "window";

    [JsonPropertyName("elements")]
    public string Elements { get; init; } = "screen";
}

internal sealed class WindowState
{
    public string StateId { get; init; } = string.Empty;

    public IntPtr Hwnd { get; init; }

    public long CreatedUnixMilliseconds { get; init; }

    public WindowRectData WindowRect { get; init; } = new();

    public string WindowTitle { get; init; } = string.Empty;

    public string? ScreenshotHash { get; init; }

    public IReadOnlyList<UiElementSnapshot> Elements { get; init; } = [];

    /// <summary>
    /// Browser-only state identity and private CDP handles. This is never
    /// serialized as part of an observation; backend node ids must not become
    /// a public MCP contract.
    /// </summary>
    public BrowserStateMetadata? Browser { get; init; }
}
