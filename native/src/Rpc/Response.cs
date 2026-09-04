using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ComputerUse.Native.Rpc;

internal sealed class RpcResponse
{
    [JsonPropertyName("id")]
    public JsonNode? Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RpcError? Error { get; init; }

    public static RpcResponse Success(JsonNode? id, JsonNode result)
    {
        return new RpcResponse
        {
            Id = id,
            Result = result
        };
    }

    public static RpcResponse Failure(JsonNode? id, string code, string message, JsonNode? details = null)
    {
        return new RpcResponse
        {
            Id = id,
            Error = new RpcError
            {
                Code = code,
                Message = message,
                Details = details
            }
        };
    }
}

internal sealed class RpcError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Details { get; init; }
}
