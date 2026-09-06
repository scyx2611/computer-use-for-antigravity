using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class BrowserObservationEngine
{
    private const int MaximumSemanticElements = 250;
    private static readonly HashSet<string> ExposedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "button",
        "checkbox",
        "combobox",
        "document",
        "heading",
        "link",
        "listitem",
        "menuitem",
        "option",
        "radio",
        "searchbox",
        "spinbutton",
        "tab",
        "textbox",
        "text"
    };

    public BrowserObservationData Observe(
        ManagedBrowserSession session,
        IntPtr hwnd,
        WindowRectData windowRect,
        WindowSnapshot window,
        CaptureEngine desktopCapture)
    {
        var targetContext = session.PrepareTarget(timeoutMilliseconds: 5_000);
        var layout = targetContext.Connection.GetLayoutMetrics();
        var viewport = ReadViewport(layout);
        var document = targetContext.Connection.GetDocument();
        var documentNodeId = ReadDocumentNodeId(document);
        session.UpdateDocumentNode(documentNodeId);
        var accessibility = targetContext.Connection.GetAccessibilityTree();
        var lifecycle = ReadLifecycleStability(targetContext.Connection, accessibility);
        var semanticSignature = ComputeSemanticSignature(
            targetContext.Target.Url,
            targetContext.Target.Title,
            accessibility);
        var elementBuild = BuildElements(
            targetContext.Connection,
            accessibility,
            targetContext.Target.Id,
            viewport);
        var elements = elementBuild.Elements;
        var screenshot = CaptureScreenshot(
            targetContext.Connection,
            session,
            hwnd,
            windowRect,
            viewport,
            desktopCapture);

        return new BrowserObservationData
        {
            Browser = new BrowserInfo
            {
                SessionId = session.SessionId,
                Browser = session.BrowserName,
                Managed = true,
                Profile = "ephemeral",
                TargetId = targetContext.Target.Id,
                Url = targetContext.Target.Url,
                Title = targetContext.Target.Title,
                Viewport = viewport,
                Lifecycle = lifecycle,
                NavigationComplete = string.Equals(lifecycle, "stable", StringComparison.Ordinal),
                DocumentGeneration = session.DocumentGeneration,
                LoaderId = session.LoaderId,
                Tabs = targetContext.Tabs.Select(ToTab).ToArray()
            },
            Elements = elements,
            ElementHandles = elementBuild.Handles,
            DocumentNodeId = documentNodeId,
            DocumentGeneration = session.DocumentGeneration,
            LoaderId = session.LoaderId,
            SemanticSignature = semanticSignature,
            Screenshot = screenshot,
            ScreenshotCoordinateSpace = screenshot.Diagnostics.Backend == "cdp_page_capture"
                ? "viewport"
                : "window"
        };
    }

    internal BrowserStabilitySample ProbeStability(
        ManagedBrowserSession session,
        int timeoutMilliseconds = 5_000)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);
        var targetContext = session.PrepareTarget(RemainingMilliseconds(deadline));
        var document = targetContext.Connection.GetDocument(RemainingMilliseconds(deadline));
        var documentNodeId = ReadDocumentNodeId(document);
        session.UpdateDocumentNode(documentNodeId);
        var accessibility = targetContext.Connection.GetAccessibilityTree(RemainingMilliseconds(deadline));
        if (documentNodeId is null
            || accessibility["nodes"] is not JsonArray)
        {
            throw new ComputerUseException(
                "BROWSER_DOCUMENT_NOT_READY",
                "The managed browser document or accessibility tree is not ready.",
                new JsonObject
                {
                    ["target_id"] = targetContext.Target.Id,
                    ["url"] = targetContext.Target.Url
                });
        }

        return new BrowserStabilitySample
        {
            TargetId = targetContext.Target.Id,
            Url = targetContext.Target.Url,
            Title = targetContext.Target.Title,
            DocumentGeneration = session.DocumentGeneration,
            LoaderId = session.LoaderId,
            DocumentNodeId = documentNodeId,
            SemanticSignature = ComputeSemanticSignature(
                targetContext.Target.Url,
                targetContext.Target.Title,
                accessibility)
        };
    }

    private static int RemainingMilliseconds(long deadline)
    {
        var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
        return remaining <= 0 ? 1 : (int)Math.Ceiling(remaining);
    }

    private static string ReadLifecycleStability(CdpConnection connection, JsonObject initialAccessibility)
    {
        try
        {
            // Keep the one-shot observation probe distinct from the workflow
            // stability loop below; this short gap prevents Chromium from
            // coalescing the two expensive accessibility snapshots.
            Thread.Sleep(100);
            var followUpAccessibility = connection.GetAccessibilityTree();
            return string.Equals(
                ComputeJsonHash(initialAccessibility),
                ComputeJsonHash(followUpAccessibility),
                StringComparison.Ordinal)
                ? "stable"
                : "changing";
        }
        catch (ComputerUseException)
        {
            return "observed";
        }
    }

    private static string ComputeJsonHash(JsonObject value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    private static BrowserElementBuildResult BuildElements(
        CdpConnection connection,
        JsonObject accessibility,
        string targetId,
        BrowserViewport viewport)
    {
        if (accessibility["nodes"] is not JsonArray nodes)
        {
            throw new ComputerUseException(
                "CDP_PROTOCOL_ERROR",
                "Accessibility.getFullAXTree returned no node array.");
        }

        var elements = new List<UiElementSnapshot>(Math.Min(nodes.Count, MaximumSemanticElements));
        var handles = new Dictionary<int, BrowserElementHandle>();
        foreach (var node in nodes.OfType<JsonObject>())
        {
            if (elements.Count >= MaximumSemanticElements || ReadBool(node, "ignored", defaultValue: true))
            {
                continue;
            }

            var role = NormalizeRole(ReadRemoteValue(node["role"]));
            if (role is null || !ExposedRoles.Contains(role))
            {
                continue;
            }

            var name = ReadRemoteValue(node["name"]) ?? string.Empty;
            var value = ReadRemoteValue(node["value"]);
            if (role is not "document" && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var bounds = ReadBounds(connection, node, role, viewport);
            if (bounds is null)
            {
                continue;
            }

            var backendNodeId = ReadInt(node["backendDOMNodeId"]);
            var dom = backendNodeId is null
                ? DomAttributes.Empty
                : ReadDomAttributes(connection, backendNodeId.Value);
            var enabled = !ReadPropertyBool(node, "disabled", defaultValue: false);
            var checkedValue = ReadOptionalPropertyBool(node, "checked");
            var selectedValue = ReadOptionalPropertyBool(node, "selected");
            var focused = ReadOptionalPropertyBool(node, "focused")
                ?? ReadOptionalBool(node, "focused")
                ?? false;
            var id = elements.Count + 1;
            var snapshot = new UiElementSnapshot
            {
                Id = id,
                Role = role,
                Name = name,
                AutomationId = dom.Id ?? string.Empty,
                Bounds = bounds,
                IsEnabled = enabled,
                Enabled = enabled,
                IsOffscreen = false,
                RuntimeId = $"browser-{targetId}-{id}",
                Value = value,
                Source = "cdp_accessibility",
                Text = string.IsNullOrWhiteSpace(name) ? value : name,
                Checked = checkedValue,
                Selected = selectedValue,
                Placeholder = dom.Placeholder,
                TestId = dom.TestId,
                IsFocused = focused
            };
            elements.Add(snapshot);

            if (backendNodeId is not null)
            {
                handles[id] = new BrowserElementHandle
                {
                    ElementId = id,
                    BackendNodeId = backendNodeId.Value,
                    FrameId = ReadString(node, "frameId"),
                    Snapshot = snapshot,
                    NodeName = dom.NodeName,
                    Placeholder = dom.Placeholder,
                    TestId = dom.TestId,
                    HtmlId = dom.Id,
                    HtmlName = dom.Name,
                    IsEditable = IsEditable(role, dom.NodeName),
                    IsFocused = focused
                };
            }
        }

        return new BrowserElementBuildResult
        {
            Elements = elements,
            Handles = handles
        };
    }

    private static int? ReadDocumentNodeId(JsonObject document)
    {
        return document["root"] is JsonObject root
            // CDP nodeId values are connection-scoped and may be refreshed by
            // DOM.getDocument. The backend node id is the stable document
            // identity we can compare across observations.
            ? ReadInt(root["backendNodeId"])
            : null;
    }

    private static bool IsEditable(string role, string? nodeName)
    {
        return role is "textbox" or "searchbox" or "combobox" or "spinbutton"
            || string.Equals(nodeName, "INPUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "TEXTAREA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "SELECT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "[contenteditable]", StringComparison.OrdinalIgnoreCase);
    }

    private static DomAttributes ReadDomAttributes(CdpConnection connection, int backendNodeId)
    {
        try
        {
            var response = connection.DescribeNode(backendNodeId);
            if (response["node"] is not JsonObject node)
            {
                return DomAttributes.Empty;
            }

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node["attributes"] is JsonArray values)
            {
                for (var index = 0; index + 1 < values.Count; index += 2)
                {
                    var name = values[index]?.GetValue<string>();
                    var value = values[index + 1]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(name) && value is not null)
                    {
                        attributes[name] = value;
                    }
                }
            }

            return new DomAttributes
            {
                NodeName = ReadString(node, "nodeName"),
                Id = ReadAttribute(attributes, "id"),
                Name = ReadAttribute(attributes, "name"),
                Placeholder = ReadAttribute(attributes, "placeholder"),
                TestId = ReadAttribute(attributes, "data-testid")
                    ?? ReadAttribute(attributes, "data-test-id")
            };
        }
        catch (ComputerUseException exception) when (exception.Code == "CDP_PROTOCOL_ERROR")
        {
            return DomAttributes.Empty;
        }
        catch (InvalidOperationException)
        {
            return DomAttributes.Empty;
        }
    }

    private static string? ReadAttribute(IReadOnlyDictionary<string, string> attributes, string name)
    {
        return attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string ComputeSemanticSignature(
        string url,
        string title,
        JsonObject accessibility)
    {
        var builder = new StringBuilder()
            .Append(url)
            .Append('\u001f')
            .Append(title)
            .Append('\u001e');
        if (accessibility["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                if (ReadBool(node, "ignored", defaultValue: true))
                {
                    continue;
                }

                builder.Append(ReadRemoteValue(node["role"]) ?? string.Empty)
                    .Append('\u001f')
                    .Append(ReadRemoteValue(node["name"]) ?? string.Empty)
                    .Append('\u001f')
                    .Append(ReadRemoteValue(node["value"]) ?? string.Empty)
                    .Append('\u001f')
                    .Append(ReadOptionalPropertyBool(node, "disabled") ?? false)
                    .Append('\u001f')
                    .Append(ReadOptionalPropertyBool(node, "checked") ?? false)
                    .Append('\u001f')
                    .Append(ReadOptionalPropertyBool(node, "selected") ?? false)
                    .Append('\u001e');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed class BrowserElementBuildResult
    {
        public IReadOnlyList<UiElementSnapshot> Elements { get; init; } = [];

        public IReadOnlyDictionary<int, BrowserElementHandle> Handles { get; init; }
            = new Dictionary<int, BrowserElementHandle>();
    }

    private sealed class DomAttributes
    {
        public static DomAttributes Empty { get; } = new();

        public string? NodeName { get; init; }

        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Placeholder { get; init; }

        public string? TestId { get; init; }
    }

    private static int[]? ReadBounds(
        CdpConnection connection,
        JsonObject node,
        string role,
        BrowserViewport viewport)
    {
        if (role == "document")
        {
            return [0, 0, viewport.Width, viewport.Height];
        }

        var backendNodeId = ReadInt(node["backendDOMNodeId"]);
        if (backendNodeId is null)
        {
            return null;
        }

        try
        {
            var response = connection.GetBoxModel(backendNodeId.Value);
            if (response["model"] is not JsonObject model)
            {
                return null;
            }

            var quad = model["border"] as JsonArray ?? model["content"] as JsonArray;
            if (quad is null || quad.Count < 8)
            {
                return null;
            }

            var points = quad
                .Select(ReadDouble)
                .ToArray();
            var left = (int)Math.Round(points.Where((_, index) => index % 2 == 0).Min());
            var top = (int)Math.Round(points.Where((_, index) => index % 2 == 1).Min());
            var right = (int)Math.Round(points.Where((_, index) => index % 2 == 0).Max());
            var bottom = (int)Math.Round(points.Where((_, index) => index % 2 == 1).Max());
            var width = Math.Max(0, right - left);
            var height = Math.Max(0, bottom - top);
            return width > 0 && height > 0 ? [left, top, width, height] : null;
        }
        catch (ComputerUseException exception) when (exception.Code == "CDP_PROTOCOL_ERROR")
        {
            // Detached or layout-only AX nodes are not actionable elements.
            return null;
        }
    }

    private static ScreenshotResult CaptureScreenshot(
        CdpConnection connection,
        ManagedBrowserSession session,
        IntPtr hwnd,
        WindowRectData windowRect,
        BrowserViewport viewport,
        CaptureEngine desktopCapture)
    {
        ComputerUseException? cdpFailure = null;
        try
        {
            var response = connection.CaptureScreenshot();
            var base64 = ReadString(response, "data");
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ComputerUseException(
                    "CDP_PROTOCOL_ERROR",
                    "Page.captureScreenshot returned no PNG data.");
            }

            var bytes = Convert.FromBase64String(base64);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new ScreenshotResult
            {
                Base64 = base64,
                Hash = hash,
                Diagnostics = new CaptureDiagnostics
                {
                    Backend = "cdp_page_capture",
                    Width = viewport.Width,
                    Height = viewport.Height,
                    Hash = hash,
                    FallbackUsed = false,
                    Errors = []
                }
            };
        }
        catch (ComputerUseException exception)
        {
            cdpFailure = exception;
        }
        catch (FormatException exception)
        {
            cdpFailure = new ComputerUseException(
                "CDP_PROTOCOL_ERROR",
                $"Page.captureScreenshot returned invalid base64 PNG data: {exception.Message}");
        }

        if (session.WindowHandle == IntPtr.Zero)
        {
            throw new ComputerUseException(
                "BROWSER_CAPTURE_UNAVAILABLE",
                "CDP page capture failed and the managed browser has no window for desktop capture fallback.",
                new JsonObject
                {
                    ["cause"] = cdpFailure?.Message
                });
        }

        var fallback = desktopCapture.Capture(hwnd, windowRect);
        var errors = new List<CaptureFailure>
        {
            new()
            {
                Backend = "cdp_page_capture",
                Error = cdpFailure?.Message ?? "CDP page capture failed."
            }
        };
        errors.AddRange(fallback.Diagnostics.Errors);

        return new ScreenshotResult
        {
            Base64 = fallback.Base64,
            Hash = fallback.Hash,
            Error = fallback.Error,
            Diagnostics = new CaptureDiagnostics
            {
                Backend = fallback.Diagnostics.Backend,
                Width = fallback.Diagnostics.Width,
                Height = fallback.Diagnostics.Height,
                Hash = fallback.Diagnostics.Hash,
                FallbackUsed = true,
                Errors = errors
            }
        };
    }

    private static BrowserViewport ReadViewport(JsonObject layout)
    {
        var visualViewport = layout["cssVisualViewport"] as JsonObject
            ?? layout["visualViewport"] as JsonObject;
        var width = Math.Max(1, ReadInt(visualViewport?["clientWidth"]) ?? 1);
        var height = Math.Max(1, ReadInt(visualViewport?["clientHeight"]) ?? 1);
        var scale = ReadDouble(visualViewport?["scale"]);
        return new BrowserViewport
        {
            Width = width,
            Height = height,
            DeviceScaleFactor = scale > 0 ? scale : 1
        };
    }

    private static BrowserTabSnapshot ToTab(CdpTargetDescriptor target)
    {
        return new BrowserTabSnapshot
        {
            TargetId = target.Id,
            Type = target.Type,
            Title = target.Title,
            Url = target.Url
        };
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return role switch
        {
            "RootWebArea" or "WebArea" => "document",
            "StaticText" => "text",
            "InlineTextBox" => null,
            _ => role.ToLowerInvariant()
        };
    }

    private static bool ReadPropertyBool(JsonObject node, string propertyName, bool defaultValue)
    {
        return ReadOptionalPropertyBool(node, propertyName) ?? defaultValue;
    }

    private static bool? ReadOptionalPropertyBool(JsonObject node, string propertyName)
    {
        if (node["properties"] is not JsonArray properties)
        {
            return null;
        }

        foreach (var property in properties.OfType<JsonObject>())
        {
            if (!string.Equals(ReadRemoteValue(property["name"]), propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ReadRemoteBool(property["value"]);
        }

        return null;
    }

    private static bool ReadBool(JsonObject node, string propertyName, bool defaultValue)
    {
        return node[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : defaultValue;
    }

    private static bool? ReadOptionalBool(JsonObject node, string propertyName)
    {
        return node[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static bool? ReadRemoteBool(JsonNode? node)
    {
        var value = ReadRemoteValue(node);
        return bool.TryParse(value, out var result) ? result : null;
    }

    private static string? ReadRemoteValue(JsonNode? node)
    {
        if (node is not JsonObject objectNode)
        {
            return node is JsonValue scalar && scalar.TryGetValue<string>(out var direct)
                ? direct
                : null;
        }

        var value = objectNode["value"];
        if (value is null)
        {
            return null;
        }

        if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToJsonString().Trim('"');
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        return value.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : null;
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<double>(out var result))
        {
            return result;
        }

        return value.TryGetValue<int>(out var integer) ? integer : 0;
    }

    private static string? ReadString(JsonObject node, string propertyName)
    {
        return node[propertyName] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }
}
