using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComputerUse.Native.Rpc;

internal sealed class RpcRequest
{
    private RpcRequest(JsonNode? id, string method, JsonObject parameters)
    {
        Id = id;
        Method = method;
        Params = parameters;
    }

    public JsonNode? Id { get; }

    public string Method { get; }

    public JsonObject Params { get; }

    public static RpcRequest Parse(string line)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new ComputerUseException("INVALID_JSON", $"Request is not valid JSON: {exception.Message}");
        }

        if (parsed is not JsonObject root)
        {
            throw new ComputerUseException("INVALID_REQUEST", "A JSON object request is required.");
        }

        var method = root["method"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ComputerUseException("INVALID_REQUEST", "Request method is required.");
        }

        var id = root["id"]?.DeepClone();
        var parametersNode = root["params"];

        if (parametersNode is null)
        {
            return new RpcRequest(id, method, new JsonObject());
        }

        if (parametersNode is not JsonObject parameters)
        {
            throw new ComputerUseException("INVALID_PARAMS", "Request params must be a JSON object.");
        }

        return new RpcRequest(id, method, parameters);
    }
}
