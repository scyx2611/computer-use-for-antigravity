using System.Text.Json.Serialization;

namespace ComputerUse.Native;

internal sealed class InteractionInfo
{
    [JsonPropertyName("surface")]
    public string Surface { get; init; } = "desktop";

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "uia";

    [JsonPropertyName("browser_detected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BrowserDetected { get; init; }

    [JsonPropertyName("browser_semantic_available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BrowserSemanticAvailable { get; init; }
}

internal sealed class BrowserViewport
{
    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("device_scale_factor")]
    public double DeviceScaleFactor { get; init; } = 1;
}

internal sealed class BrowserTabSnapshot
{
    [JsonPropertyName("target_id")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "page";

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

internal sealed class BrowserInfo
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("browser")]
    public string Browser { get; init; } = string.Empty;

    [JsonPropertyName("managed")]
    public bool Managed { get; init; } = true;

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "ephemeral";

    [JsonPropertyName("target_id")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("viewport")]
    public BrowserViewport Viewport { get; init; } = new();

    [JsonPropertyName("lifecycle")]
    public string Lifecycle { get; init; } = "stable";

    [JsonPropertyName("document_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentGeneration { get; init; }

    [JsonPropertyName("loader_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoaderId { get; init; }

    [JsonPropertyName("tabs")]
    public IReadOnlyList<BrowserTabSnapshot> Tabs { get; init; } = [];
}

internal sealed class BrowserObservationData
{
    public BrowserInfo Browser { get; init; } = new();

    public IReadOnlyList<UiElementSnapshot> Elements { get; init; } = [];

    public IReadOnlyDictionary<int, BrowserElementHandle> ElementHandles { get; init; }
        = new Dictionary<int, BrowserElementHandle>();

    public int? DocumentNodeId { get; init; }

    public string DocumentGeneration { get; init; } = string.Empty;

    public string? FrameId { get; init; }

    public string? LoaderId { get; init; }

    public string SemanticSignature { get; init; } = string.Empty;

    public ScreenshotResult Screenshot { get; init; } = new();

    public string ScreenshotCoordinateSpace { get; init; } = "viewport";
}

/// <summary>
/// Private mapping from the public observation element id to the CDP node.
/// Backend ids are document-scoped and are intentionally never serialized.
/// </summary>
internal sealed class BrowserElementHandle
{
    public int ElementId { get; init; }

    public int BackendNodeId { get; init; }

    public string? FrameId { get; init; }

    public UiElementSnapshot Snapshot { get; init; } = new();

    public string? NodeName { get; init; }

    public string? Placeholder { get; init; }

    public string? TestId { get; init; }

    public string? HtmlId { get; init; }

    public string? HtmlName { get; init; }

    public bool IsEditable { get; init; }

    public bool IsFocused { get; init; }
}

internal sealed class BrowserStateMetadata
{
    public string SessionId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string? FrameId { get; init; }

    public string? LoaderId { get; init; }

    public string DocumentGeneration { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int? DocumentNodeId { get; init; }

    public string SemanticSignature { get; init; } = string.Empty;

    public IReadOnlyDictionary<int, BrowserElementHandle> ElementHandles { get; init; }
        = new Dictionary<int, BrowserElementHandle>();
}

internal sealed class BrowserLaunchResult
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("started")]
    public bool Started { get; init; }

    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pid { get; init; }

    [JsonPropertyName("window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowSnapshot? Window { get; init; }

    [JsonPropertyName("browser")]
    public BrowserLaunchInfo Browser { get; init; } = new();
}

internal sealed class BrowserLaunchInfo
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("browser")]
    public string Browser { get; init; } = string.Empty;

    [JsonPropertyName("managed")]
    public bool Managed { get; init; } = true;

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "ephemeral";

    [JsonPropertyName("target_id")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("window_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowId { get; init; }

    [JsonPropertyName("debug_endpoint")]
    public string DebugEndpoint { get; init; } = string.Empty;
}
