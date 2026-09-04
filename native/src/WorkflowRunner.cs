using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class WorkflowRunner
{
    public WorkflowExecutionResult Run(
        IWorkflowDriver driver,
        IReadOnlyList<JsonObject> actions,
        bool verify)
    {
        ValidateWorkflow(actions);

        var records = new List<ActionRecord>(actions.Count);
        var trace = new List<ExecutionTraceEntry>();
        WorkflowObservation? current = null;

        for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
        {
            var action = actions[actionIndex];
            var actionType = RetryPolicy.ReadActionType(action);
            var retryPolicy = RetryPolicy.Read(action);
            var staleRecoveryUsed = false;

            for (var attempt = 1; ; attempt++)
            {
                var started = Stopwatch.GetTimestamp();
                WorkflowObservation? before = null;
                WorkflowObservation? after = null;
                ActionExecutionResult? execution = null;
                UiStabilityResult? stability = null;
                PostconditionVerification? verification = null;
                ComputerUseException? failure = null;
                var actionExecuted = false;

                try
                {
                    driver.EnsureInputAllowed();
                    before = driver.Observe();
                    execution = driver.Execute(before, action);
                    actionExecuted = execution.ActionExecuted;
                    stability = driver.WaitForUiStable(before);
                    after = driver.Observe();

                    if (!stability.IsStable)
                    {
                        verification = PostconditionVerification.FailedResult(
                            "UI_NOT_STABLE",
                            "The UI did not stabilize before the bounded timeout.",
                            [new PostconditionCheck
                            {
                                Type = "ui_stable",
                                Passed = false,
                                Expected = JsonValue.Create(true),
                                Actual = JsonValue.Create(false),
                                Message = "The UI did not stabilize before the bounded timeout."
                            }]);
                        failure = new ComputerUseException(
                            "UI_NOT_STABLE",
                            "The UI did not stabilize before the bounded timeout.");
                    }
                    else
                    {
                        try
                        {
                            verification = PostconditionEvaluator.Evaluate(
                                action["expect"],
                                before,
                                after,
                                stability);
                        }
                        catch (ComputerUseException exception) when (exception.Code == "AMBIGUOUS_TARGET")
                        {
                            // The action already completed. Refresh only the verification
                            // observation; never execute the action again for this ambiguity.
                            after = driver.Observe();
                            verification = PostconditionEvaluator.Evaluate(
                                action["expect"],
                                before,
                                after,
                                stability);
                        }

                        if (!verification.Passed)
                        {
                            failure = new ComputerUseException(
                                verification.ErrorCode ?? "POSTCONDITION_FAILED",
                                verification.Message ?? "Postcondition failed.",
                                JsonSerializer.SerializeToNode(verification));
                        }
                    }

                    if (failure is null)
                    {
                        trace.Add(CreateTrace(
                            actionIndex,
                            actionType,
                            attempt,
                            action,
                            before,
                            after,
                            execution,
                            verification,
                            null,
                            "passed",
                            null,
                            started));
                        records.Add(execution.Record);
                        current = after;
                        break;
                    }
                }
                catch (ComputerUseException exception)
                {
                    failure = exception;
                }
                catch (Exception exception)
                {
                    failure = new ComputerUseException(
                        "WORKFLOW_STEP_FAILED",
                        $"Workflow step failed unexpectedly: {exception.Message}");
                }

                actionExecuted = execution?.ActionExecuted ?? actionExecuted;
                var retryDecision = DecideRetry(
                    failure,
                    action,
                    actionType,
                    retryPolicy,
                    attempt,
                    actionExecuted,
                    ref staleRecoveryUsed);

                trace.Add(CreateTrace(
                    actionIndex,
                    actionType,
                    attempt,
                    action,
                    before,
                    after,
                    execution,
                    verification,
                    failure,
                    retryDecision.CanRetry ? "retrying" : "failed",
                    retryDecision.Reason,
                    started));

                if (retryDecision.CanRetry)
                {
                    if (retryPolicy.DelayMilliseconds > 0)
                    {
                        Thread.Sleep(retryPolicy.DelayMilliseconds);
                    }

                    continue;
                }

                throw BuildFailure(
                    actionIndex,
                    actionType,
                    attempt,
                    retryPolicy,
                    actionExecuted,
                    after ?? before ?? current,
                    failure,
                    verification,
                    trace,
                    retryDecision);
            }
        }

        var finalObservation = verify
            ? current ?? driver.Observe()
            : null;

        return new WorkflowExecutionResult
        {
            Actions = records,
            ExecutionTrace = trace,
            FinalObservation = finalObservation,
            Verified = verify
        };
    }

    private static void ValidateWorkflow(IReadOnlyList<JsonObject> actions)
    {
        foreach (var action in actions)
        {
            _ = RetryPolicy.ReadActionType(action);
            _ = RetryPolicy.Read(action);
            PostconditionEvaluator.Validate(action["expect"]);
        }
    }

    private static RetryDecision DecideRetry(
        ComputerUseException? failure,
        JsonObject action,
        string actionType,
        RetryPolicy retryPolicy,
        int attempt,
        bool actionExecuted,
        ref bool staleRecoveryUsed)
    {
        if (failure is null || string.Equals(failure.Code, "TARGET_ELEVATED", StringComparison.Ordinal))
        {
            return RetryDecision.NoRetry;
        }

        if (!actionExecuted
            && failure.Code is "STALE_STATE" or "STALE_BROWSER_STATE" or "BROWSER_FRAME_DETACHED"
            && !staleRecoveryUsed)
        {
            staleRecoveryUsed = true;
            return new RetryDecision(true, "stale_state_reobserve", true, false);
        }

        if (!actionExecuted && RetryPolicy.IsRetryableBeforeExecution(failure.Code, action))
        {
            var canRetry = attempt < retryPolicy.MaxAttempts;
            return new RetryDecision(
                canRetry,
                canRetry ? $"{failure.Code.ToLowerInvariant()}_reobserve" : null,
                true,
                !canRetry);
        }

        if (actionExecuted
            && attempt < retryPolicy.MaxAttempts
            && RetryPolicy.IsSafeToRetryAfterExecution(actionType)
            && (failure.Code is "POSTCONDITION_FAILED" or "UI_NOT_STABLE"))
        {
            return new RetryDecision(true, "postcondition_or_stability_retry", true, false);
        }

        var retryAllowed = !actionExecuted
            ? RetryPolicy.IsRetryableBeforeExecution(failure.Code, action)
            : RetryPolicy.IsSafeToRetryAfterExecution(actionType)
                && (failure.Code is "POSTCONDITION_FAILED" or "UI_NOT_STABLE");
        return RetryDecision.NoRetry with
        {
            RetryAllowed = retryAllowed,
            RetryExhausted = retryAllowed && attempt >= retryPolicy.MaxAttempts
        };
    }

    private static ExecutionTraceEntry CreateTrace(
        int actionIndex,
        string actionType,
        int attempt,
        JsonObject action,
        WorkflowObservation? before,
        WorkflowObservation? after,
        ActionExecutionResult? execution,
        PostconditionVerification? verification,
        ComputerUseException? failure,
        string finalStatus,
        string? retryReason,
        long started)
    {
        var captureBackend = after?.Result.Capture.Backend ?? before?.Result.Capture.Backend;
        return new ExecutionTraceEntry
        {
            StepIndex = actionIndex + 1,
            ActionType = actionType,
            Attempt = attempt,
            TargetRequested = ExtractTarget(action),
            Resolution = execution?.Resolution,
            CaptureBackend = captureBackend,
            StateIdBefore = before?.Result.StateId,
            StateIdAfter = after?.Result.StateId,
            ScreenshotHashBefore = before?.Result.ScreenshotHash,
            ScreenshotHashAfter = after?.Result.ScreenshotHash,
            Postcondition = action["expect"]?.DeepClone(),
            Verification = verification,
            RetryReason = retryReason,
            DurationMilliseconds = ElapsedMilliseconds(started),
            FinalStatus = finalStatus,
            ActionExecuted = execution?.ActionExecuted ?? false,
            ErrorCode = failure?.Code,
            ErrorMessage = failure?.Message
        };
    }

    private static ComputerUseException BuildFailure(
        int actionIndex,
        string actionType,
        int attempt,
        RetryPolicy retryPolicy,
        bool actionExecuted,
        WorkflowObservation? lastObservation,
        ComputerUseException? failure,
        PostconditionVerification? verification,
        IReadOnlyList<ExecutionTraceEntry> trace,
        RetryDecision retryDecision)
    {
        var code = failure?.Code ?? "WORKFLOW_STEP_FAILED";
        var message = failure?.Message ?? "Workflow step failed.";
        var details = new JsonObject
        {
            ["step_index"] = actionIndex + 1,
            ["action_type"] = actionType,
            ["attempt"] = attempt,
            ["attempts_used"] = attempt,
            ["retry_max_attempts"] = retryPolicy.MaxAttempts,
            ["retry_allowed"] = retryDecision.RetryAllowed,
            ["retry_exhausted"] = retryDecision.RetryExhausted,
            ["action_executed"] = actionExecuted,
            ["last_observation"] = ObservationSummary(lastObservation),
            ["execution_trace"] = JsonSerializer.SerializeToNode(trace)
        };

        if (verification is not null)
        {
            details["verification"] = JsonSerializer.SerializeToNode(verification);
        }

        if (failure?.Details is not null)
        {
            details["cause_details"] = failure.Details.DeepClone();
        }

        return new ComputerUseException(
            code,
            $"Workflow step {actionIndex + 1} ({actionType}) failed: {message}",
            details);
    }

    private static JsonObject ObservationSummary(WorkflowObservation? observation)
    {
        if (observation is null)
        {
            return new JsonObject
            {
                ["available"] = false
            };
        }

        var result = observation.Result;
        return new JsonObject
        {
            ["available"] = true,
            ["state_id"] = result.StateId,
            ["window"] = JsonSerializer.SerializeToNode(result.Window),
            ["screenshot_hash"] = result.ScreenshotHash,
            ["capture"] = JsonSerializer.SerializeToNode(result.Capture),
            ["element_count"] = result.Elements.Count,
            ["ui_signature"] = observation.UiSignature
        };
    }

    private static JsonNode? ExtractTarget(JsonObject action)
    {
        if (action["target"] is not null)
        {
            return action["target"]!.DeepClone();
        }

        var selected = new JsonObject();
        foreach (var name in new[]
        {
            "element_id", "name", "role", "automation_id", "x", "y", "coordinate_space",
            "css", "test_id", "placeholder", "start_target", "end_target", "start", "end"
        })
        {
            if (action[name] is not null)
            {
                selected[name] = action[name]!.DeepClone();
            }
        }

        if (action["target"] is JsonObject targetObject && targetObject["text"] is not null)
        {
            selected["text"] = targetObject["text"]!.DeepClone();
        }

        return selected.Count == 0 ? null : selected;
    }

    private static long ElapsedMilliseconds(long startTimestamp)
    {
        return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);
    }

    private readonly record struct RetryDecision(
        bool CanRetry,
        string? Reason,
        bool RetryAllowed,
        bool RetryExhausted)
    {
        public static RetryDecision NoRetry => new(false, null, false, false);
    }
}
