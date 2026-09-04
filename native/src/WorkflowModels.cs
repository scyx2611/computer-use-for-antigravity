using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ComputerUse.Native;

internal sealed class ActionExecutionResult
{
    public ActionExecutionResult(ActionRecord record, JsonNode? resolution, bool actionExecuted)
    {
        Record = record;
        Resolution = resolution;
        ActionExecuted = actionExecuted;
    }

    public ActionRecord Record { get; }

    public JsonNode? Resolution { get; }

    public bool ActionExecuted { get; }
}

internal sealed class WorkflowObservation
{
    public WorkflowObservation(ObserveResult result, string? uiSignature = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        UiSignature = string.IsNullOrWhiteSpace(uiSignature)
            ? UiStateSignature.Compute(result)
            : uiSignature;
    }

    public ObserveResult Result { get; }

    public string UiSignature { get; }
}

internal interface IWorkflowDriver
{
    void EnsureInputAllowed();

    WorkflowObservation Observe();

    ActionExecutionResult Execute(WorkflowObservation observation, JsonObject action);

    UiStabilityResult WaitForUiStable(WorkflowObservation before);
}

internal sealed class DelegateWorkflowDriver : IWorkflowDriver
{
    private readonly Action ensureInputAllowed;
    private readonly Func<WorkflowObservation> observe;
    private readonly Func<WorkflowObservation, JsonObject, ActionExecutionResult> execute;
    private readonly Func<WorkflowObservation, UiStabilityResult> waitForUiStable;

    public DelegateWorkflowDriver(
        Action ensureInputAllowed,
        Func<WorkflowObservation> observe,
        Func<WorkflowObservation, JsonObject, ActionExecutionResult> execute,
        Func<WorkflowObservation, UiStabilityResult> waitForUiStable)
    {
        this.ensureInputAllowed = ensureInputAllowed ?? throw new ArgumentNullException(nameof(ensureInputAllowed));
        this.observe = observe ?? throw new ArgumentNullException(nameof(observe));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.waitForUiStable = waitForUiStable ?? throw new ArgumentNullException(nameof(waitForUiStable));
    }

    public void EnsureInputAllowed() => ensureInputAllowed();

    public WorkflowObservation Observe() => observe();

    public ActionExecutionResult Execute(WorkflowObservation observation, JsonObject action)
        => execute(observation, action);

    public UiStabilityResult WaitForUiStable(WorkflowObservation before)
        => waitForUiStable(before);
}

internal sealed class UiStabilityResult
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "timeout";

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMilliseconds { get; init; }

    [JsonPropertyName("initial_signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitialSignature { get; init; }

    [JsonPropertyName("final_signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinalSignature { get; init; }

    [JsonIgnore]
    public bool IsStable => string.Equals(Status, "stable", StringComparison.Ordinal);
}

internal sealed class PostconditionVerification
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "not_requested";

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<PostconditionCheck> Checks { get; init; } = [];

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    public static PostconditionVerification NotRequested()
    {
        return new PostconditionVerification
        {
            Status = "not_requested",
            Passed = true
        };
    }

    public static PostconditionVerification PassedResult(IReadOnlyList<PostconditionCheck> checks)
    {
        return new PostconditionVerification
        {
            Status = "passed",
            Passed = true,
            Checks = checks,
            Message = "All postconditions passed."
        };
    }

    public static PostconditionVerification FailedResult(
        string errorCode,
        string message,
        IReadOnlyList<PostconditionCheck> checks)
    {
        return new PostconditionVerification
        {
            Status = "failed",
            Passed = false,
            Checks = checks,
            Message = message,
            ErrorCode = errorCode
        };
    }
}

internal sealed class PostconditionCheck
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("expected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Expected { get; init; }

    [JsonPropertyName("actual")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Actual { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed class ExecutionTraceEntry
{
    [JsonPropertyName("step_index")]
    public int StepIndex { get; init; }

    [JsonPropertyName("action_type")]
    public string ActionType { get; init; } = string.Empty;

    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("target_requested")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? TargetRequested { get; init; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Resolution { get; init; }

    [JsonPropertyName("capture_backend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureBackend { get; init; }

    [JsonPropertyName("state_id_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateIdBefore { get; init; }

    [JsonPropertyName("state_id_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateIdAfter { get; init; }

    [JsonPropertyName("screenshot_hash_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotHashBefore { get; init; }

    [JsonPropertyName("screenshot_hash_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotHashAfter { get; init; }

    [JsonPropertyName("postcondition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Postcondition { get; init; }

    [JsonPropertyName("verification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PostconditionVerification? Verification { get; init; }

    [JsonPropertyName("retry_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryReason { get; init; }

    [JsonPropertyName("duration_ms")]
    public long DurationMilliseconds { get; init; }

    [JsonPropertyName("final_status")]
    public string FinalStatus { get; init; } = string.Empty;

    [JsonPropertyName("action_executed")]
    public bool ActionExecuted { get; init; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }
}

internal sealed class WorkflowExecutionResult
{
    public IReadOnlyList<ActionRecord> Actions { get; init; } = [];

    public IReadOnlyList<ExecutionTraceEntry> ExecutionTrace { get; init; } = [];

    public WorkflowObservation? FinalObservation { get; init; }

    public bool Verified { get; init; }
}
