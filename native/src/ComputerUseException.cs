using System.Text.Json.Nodes;

namespace ComputerUse.Native;

/// <summary>
/// An expected, model-actionable runtime failure.
/// </summary>
internal sealed class ComputerUseException : Exception
{
    public ComputerUseException(string code, string message, JsonNode? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonNode? Details { get; }
}
