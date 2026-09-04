using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ComputerUse.Native;

internal sealed class CdpTargetDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("webSocketDebuggerUrl")]
    public string WebSocketDebuggerUrl { get; init; } = string.Empty;
}

internal sealed class CdpEndpointClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient http;
    private readonly Uri listUri;

    public CdpEndpointClient(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(2_000)
        };
        listUri = new Uri($"http://127.0.0.1:{port}/json/list", UriKind.Absolute);
    }

    public int Port { get; }

    public string DebugEndpoint => $"127.0.0.1:{Port}";

    public IReadOnlyList<CdpTargetDescriptor> ListPageTargets()
    {
        try
        {
            var json = http.GetStringAsync(listUri).GetAwaiter().GetResult();
            var targets = JsonSerializer.Deserialize<List<CdpTargetDescriptor>>(json, SerializerOptions);
            if (targets is null)
            {
                throw new ComputerUseException(
                    "CDP_PROTOCOL_ERROR",
                    "Chromium returned an empty target list.");
            }

            return targets
                .Where(target => string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (ComputerUseException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ComputerUseException(
                "CDP_PROTOCOL_ERROR",
                $"Chromium returned invalid target metadata: {exception.Message}");
        }
        catch (TaskCanceledException exception)
        {
            throw new ComputerUseException(
                "CDP_TIMEOUT",
                $"Timed out reading the Chromium target list: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            throw new ComputerUseException(
                "BROWSER_BACKEND_UNAVAILABLE",
                $"Unable to reach the managed Chromium CDP endpoint: {exception.Message}");
        }
    }

    public void Dispose()
    {
        http.Dispose();
    }
}

/// <summary>
/// Restricted page-target CDP transport. It intentionally exposes only the
/// commands needed by browser observation and deterministic input; callers
/// cannot submit an arbitrary CDP method or JavaScript expression through this
/// class.
/// </summary>
internal sealed class CdpConnection : IDisposable
{
    private readonly object sync = new();
    private readonly ClientWebSocket socket;
    private int nextId;
    private bool disposed;

    private CdpConnection(ClientWebSocket socket)
    {
        this.socket = socket;
    }

    public bool IsOpen => !disposed && socket.State == WebSocketState.Open;

    public static CdpConnection Connect(string endpoint, int timeoutMilliseconds)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss)
            || !IsLoopback(uri.Host))
        {
            throw new ComputerUseException(
                "CDP_CONNECTION_FAILED",
                "Managed browser CDP endpoints must be local websocket URLs.");
        }

        var socket = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            }
        };

        try
        {
            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
            socket.ConnectAsync(uri, cancellation.Token).GetAwaiter().GetResult();
            return new CdpConnection(socket);
        }
        catch (OperationCanceledException exception)
        {
            socket.Dispose();
            throw new ComputerUseException(
                "CDP_TIMEOUT",
                $"Timed out connecting to the managed browser CDP target: {exception.Message}");
        }
        catch (Exception exception) when (exception is WebSocketException or InvalidOperationException)
        {
            socket.Dispose();
            throw new ComputerUseException(
                "CDP_CONNECTION_FAILED",
                $"Unable to connect to the managed browser CDP target: {exception.Message}");
        }
    }

    public JsonObject GetLayoutMetrics(int timeoutMilliseconds = 5_000)
        => SendCommand("Page.getLayoutMetrics", null, timeoutMilliseconds);

    public JsonObject GetAccessibilityTree(int timeoutMilliseconds = 5_000)
        => SendCommand("Accessibility.getFullAXTree", null, timeoutMilliseconds);

    public JsonObject GetBoxModel(int backendNodeId, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.getBoxModel",
            new JsonObject { ["backendNodeId"] = backendNodeId },
            timeoutMilliseconds);

    public JsonObject GetDocument(int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.getDocument",
            new JsonObject
            {
                ["depth"] = 0,
                ["pierce"] = false
            },
            timeoutMilliseconds);

    public JsonObject QuerySelectorAll(int nodeId, string selector, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.querySelectorAll",
            new JsonObject
            {
                ["nodeId"] = nodeId,
                ["selector"] = selector
            },
            timeoutMilliseconds);

    public JsonObject DescribeNode(int backendNodeId, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.describeNode",
            new JsonObject
            {
                ["backendNodeId"] = backendNodeId,
                ["depth"] = 0,
                ["pierce"] = false
            },
            timeoutMilliseconds);

    public JsonObject DescribeNodeByNodeId(int nodeId, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.describeNode",
            new JsonObject
            {
                ["nodeId"] = nodeId,
                ["depth"] = 0,
                ["pierce"] = false
            },
            timeoutMilliseconds);

    public JsonObject ScrollIntoViewIfNeeded(int backendNodeId, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.scrollIntoViewIfNeeded",
            new JsonObject { ["backendNodeId"] = backendNodeId },
            timeoutMilliseconds);

    public JsonObject Focus(int backendNodeId, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "DOM.focus",
            new JsonObject { ["backendNodeId"] = backendNodeId },
            timeoutMilliseconds);

    public void DispatchMouseEvent(
        string type,
        double x,
        double y,
        string button = "none",
        int buttons = 0,
        int clickCount = 0,
        double deltaX = 0,
        double deltaY = 0,
        int timeoutMilliseconds = 5_000)
    {
        var parameters = new JsonObject
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["buttons"] = buttons,
            ["clickCount"] = clickCount,
            ["deltaX"] = deltaX,
            ["deltaY"] = deltaY,
            ["pointerType"] = "mouse"
        };
        _ = SendCommand("Input.dispatchMouseEvent", parameters, timeoutMilliseconds);
    }

    public void InsertText(string text, int timeoutMilliseconds = 5_000)
    {
        _ = SendCommand(
            "Input.insertText",
            new JsonObject { ["text"] = text },
            timeoutMilliseconds);
    }

    public void DispatchKeyEvent(
        string type,
        string key,
        string code,
        int modifiers,
        int windowsVirtualKeyCode,
        string? text = null,
        int timeoutMilliseconds = 5_000)
    {
        var parameters = new JsonObject
        {
            ["type"] = type,
            ["key"] = key,
            ["code"] = code,
            ["modifiers"] = modifiers,
            ["windowsVirtualKeyCode"] = windowsVirtualKeyCode,
            ["nativeVirtualKeyCode"] = windowsVirtualKeyCode,
            ["autoRepeat"] = false,
            ["isKeypad"] = false,
            ["isSystemKey"] = false
        };
        if (text is not null && (type == "keyDown" || type == "char"))
        {
            parameters["text"] = text;
        }

        _ = SendCommand("Input.dispatchKeyEvent", parameters, timeoutMilliseconds);
    }

    public JsonObject CaptureScreenshot(int timeoutMilliseconds = 5_000)
        => SendCommand(
            "Page.captureScreenshot",
            new JsonObject
            {
                ["format"] = "png",
                ["fromSurface"] = true
            },
            timeoutMilliseconds);

    public JsonObject Navigate(string url, int timeoutMilliseconds = 5_000)
        => SendCommand(
            "Page.navigate",
            new JsonObject { ["url"] = url },
            timeoutMilliseconds);

    private JsonObject SendCommand(string method, JsonObject? parameters, int timeoutMilliseconds)
    {
        lock (sync)
        {
            if (!IsOpen)
            {
                throw new ComputerUseException(
                    "BROWSER_SESSION_CLOSED",
                    "The managed browser CDP connection is closed.");
            }

            var id = ++nextId;
            var request = new JsonObject
            {
                ["id"] = id,
                ["method"] = method
            };
            if (parameters is not null)
            {
                request["params"] = parameters.DeepClone();
            }

            var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            try
            {
                using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
                socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellation.Token).GetAwaiter().GetResult();

                while (true)
                {
                    var message = ReceiveMessage(cancellation.Token);
                    if (message is null)
                    {
                        throw new ComputerUseException(
                            "BROWSER_SESSION_CLOSED",
                            "The managed browser closed its CDP target connection.");
                    }

                    if (!TryReadMessageId(message, out var responseId) || responseId != id)
                    {
                        // CDP events are expected during navigation and are
                        // deliberately ignored by this synchronous spike.
                        continue;
                    }

                    if (message["error"] is JsonObject error)
                    {
                        var errorCode = ReadString(error, "code") ?? "unknown";
                        var errorMessage = ReadString(error, "message") ?? "CDP command failed.";
                        var mappedCode = MapProtocolError(method, errorMessage);
                        throw new ComputerUseException(
                            mappedCode,
                            $"CDP command '{method}' failed ({errorCode}): {errorMessage}",
                            new JsonObject
                            {
                                ["method"] = method,
                                ["error_code"] = mappedCode,
                                ["cdp_code"] = errorCode,
                                ["cdp_message"] = errorMessage,
                                ["data"] = error["data"]?.DeepClone()
                            });
                    }

                    return message["result"] as JsonObject ?? new JsonObject();
                }
            }
            catch (ComputerUseException)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw new ComputerUseException(
                    "CDP_TIMEOUT",
                    $"CDP command '{method}' timed out after {timeoutMilliseconds} ms: {exception.Message}");
            }
            catch (WebSocketException exception)
            {
                throw new ComputerUseException(
                    "BROWSER_SESSION_CLOSED",
                    $"CDP command '{method}' could not use the browser connection: {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                throw new ComputerUseException(
                    "BROWSER_SESSION_CLOSED",
                    $"CDP command '{method}' could not use the browser connection: {exception.Message}");
            }
        }
    }

    private JsonObject? ReceiveMessage(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var result = socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).GetAwaiter().GetResult();

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())) as JsonObject
                ?? throw new ComputerUseException(
                    "CDP_PROTOCOL_ERROR",
                    "Chromium returned a non-object CDP message.");
        }
        catch (JsonException exception)
        {
            throw new ComputerUseException(
                "CDP_PROTOCOL_ERROR",
                $"Chromium returned invalid CDP JSON: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                socket.Abort();
            }
            finally
            {
                socket.Dispose();
            }
        }
    }

    private static bool TryReadMessageId(JsonObject message, out int id)
    {
        id = 0;
        return message["id"] is JsonValue value && value.TryGetValue(out id);
    }

    private static string? ReadString(JsonObject objectNode, string propertyName)
    {
        return objectNode[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static string MapProtocolError(string method, string message)
    {
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("target closed", StringComparison.Ordinal)
            || normalized.Contains("target crashed", StringComparison.Ordinal)
            || normalized.Contains("session closed", StringComparison.Ordinal)
            || normalized.Contains("browser has been closed", StringComparison.Ordinal))
        {
            return "BROWSER_TARGET_CLOSED";
        }

        if (normalized.Contains("frame detached", StringComparison.Ordinal)
            || normalized.Contains("frame was detached", StringComparison.Ordinal))
        {
            return "BROWSER_FRAME_DETACHED";
        }

        if (normalized.Contains("execution context was destroyed", StringComparison.Ordinal)
            || normalized.Contains("cannot find context", StringComparison.Ordinal)
            || normalized.Contains("no node with given id", StringComparison.Ordinal)
            || normalized.Contains("could not find node", StringComparison.Ordinal)
            || normalized.Contains("node with given id", StringComparison.Ordinal)
            || normalized.Contains("object reference chain is too long", StringComparison.Ordinal))
        {
            return "STALE_BROWSER_STATE";
        }

        return "CDP_PROTOCOL_ERROR";
    }

    private static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
