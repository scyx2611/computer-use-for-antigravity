using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class RetryPolicy
{
    public const int MaximumAttempts = 5;
    private const int DefaultAttempts = 1;
    private const int DefaultDelayMilliseconds = 150;

    private RetryPolicy(int maxAttempts, int delayMilliseconds)
    {
        MaxAttempts = maxAttempts;
        DelayMilliseconds = delayMilliseconds;
    }

    public int MaxAttempts { get; }

    public int DelayMilliseconds { get; }

    public static RetryPolicy Read(JsonObject action)
    {
        var node = action["retry"];
        if (node is null)
        {
            return new RetryPolicy(DefaultAttempts, DefaultDelayMilliseconds);
        }

        if (node is not JsonObject retry)
        {
            throw new ComputerUseException("INVALID_ACTION", "retry must be a JSON object.");
        }

        var maxAttempts = ReadInt(retry, "max_attempts", DefaultAttempts, 1, MaximumAttempts);
        var delayMilliseconds = ReadInt(retry, "delay_ms", DefaultDelayMilliseconds, 0, 5_000);
        return new RetryPolicy(maxAttempts, delayMilliseconds);
    }

    public static string ReadActionType(JsonObject action)
    {
        var node = action["type"];
        if (node is JsonValue value
            && value.TryGetValue<string>(out var type)
            && !string.IsNullOrWhiteSpace(type))
        {
            return type.ToLowerInvariant();
        }

        throw new ComputerUseException("INVALID_ACTION", "type is required and must be a non-empty string.");
    }

    public static bool IsRetryableBeforeExecution(string code, JsonObject action)
    {
        return code switch
        {
            "TARGET_NOT_FOUND" => true,
            "ELEMENT_NOT_FOUND" => true,
            "UIA_UNAVAILABLE" => true,
            "AMBIGUOUS_TARGET" => true,
            "ELEMENT_DISABLED" => HasElementEnabledExpectation(action),
            _ => false
        };
    }

    public static bool IsSafeToRetryAfterExecution(string actionType)
    {
        return actionType switch
        {
            "set_value" => true,
            "wait" => true,
            _ => false
        };
    }

    private static bool HasElementEnabledExpectation(JsonObject action)
    {
        return action["expect"] is JsonObject expect
            && expect["element_enabled"] is not null;
    }

    private static int ReadInt(
        JsonObject objectNode,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
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
            throw new ComputerUseException(
                "INVALID_ACTION",
                $"{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }
}
