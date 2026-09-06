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
                            stability,
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

                actionExecuted = execution?.ActionExecuted
                    ?? ReadActionExecuted(failure?.Details)
                    ?? actionExecuted;
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
                    stability,
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
        for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
        {
            var action = actions[actionIndex];
            string? actionType = null;
            try
            {
                actionType = RetryPolicy.ReadActionType(action);
                _ = RetryPolicy.Read(action);
                PostconditionEvaluator.Validate(action["expect"]);
            }
            catch (ComputerUseException exception)
            {
                throw BuildValidationFailure(actionIndex, actionType, exception);
            }
        }
    }

    private static ComputerUseException BuildValidationFailure(
        int actionIndex,
        string? actionType,
        ComputerUseException failure)
    {
        var details = failure.Details as JsonObject is { } originalDetails
            ? (JsonObject)originalDetails.DeepClone()
            : new JsonObject();

        details["step_index"] = actionIndex + 1;
        details["action_type"] = actionType ?? "unknown";
        details["attempt"] = 0;
        details["attempts_used"] = 0;
        details["retry_allowed"] = false;
        details["retry_exhausted"] = false;
        details["action_executed"] = false;
        details["action_execution_status"] = "not_executed";
        details["last_observation"] = new JsonObject { ["available"] = false };
        details["session"] = new JsonObject { ["surface"] = "unknown", ["valid"] = false };
        details["execution_trace"] = new JsonArray();
        if (failure.Details is not null)
        {
            details["cause_details"] = failure.Details.DeepClone();
        }

        return new ComputerUseException(
            failure.Code,
            $"Workflow step {actionIndex + 1} is invalid: {failure.Message}",
            details);
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
            && !staleRecoveryUsed
            && attempt < retryPolicy.MaxAttempts)
        {
            staleRecoveryUsed = true;
            return new RetryDecision(true, "stale_state_reobserve", true, false);
        }

        if (!actionExecuted && RetryPolicy.IsRetryableBeforeExecution(failure.Code, action, failure.Details))
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
            ? RetryPolicy.IsRetryableBeforeExecution(failure.Code, action, failure.Details)
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
        UiStabilityResult? stability,
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
            Surface = before?.Result.Interaction.Surface ?? after?.Result.Interaction.Surface,
            Backend = before?.Result.Interaction.Backend ?? after?.Result.Interaction.Backend,
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
            Stability = stability,
            Navigation = CreateNavigationTrace(before, after, stability),
            RetryReason = retryReason,
            DurationMilliseconds = ElapsedMilliseconds(started),
            FinalStatus = finalStatus,
            ActionExecuted = execution?.ActionExecuted
                ?? ReadActionExecuted(failure?.Details)
                ?? false,
            ActionExecutionStatus = ReadActionExecutionStatus(execution, failure),
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
            ["action_execution_status"] = actionExecuted
                ? "executed"
                : ReadActionExecutionStatus(null, failure),
            ["last_observation"] = ObservationSummary(lastObservation),
            ["session"] = SessionSummary(lastObservation, failure),
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

    private static NavigationTrace? CreateNavigationTrace(
        WorkflowObservation? before,
        WorkflowObservation? after,
        UiStabilityResult? stability)
    {
        if (before?.Result.Browser is null && after?.Result.Browser is null)
        {
            return null;
        }

        var beforeBrowser = before?.Result.Browser;
        var afterBrowser = after?.Result.Browser;
        var occurred = stability?.NavigationOccurred
            ?? (beforeBrowser is not null
                && afterBrowser is not null
                && (!string.Equals(beforeBrowser.TargetId, afterBrowser.TargetId, StringComparison.Ordinal)
                    || !string.Equals(beforeBrowser.DocumentGeneration, afterBrowser.DocumentGeneration, StringComparison.Ordinal)
                    || !string.Equals(beforeBrowser.Url, afterBrowser.Url, StringComparison.Ordinal)));

        return new NavigationTrace
        {
            Occurred = occurred,
            Complete = stability?.NavigationComplete
                ?? afterBrowser?.NavigationComplete
                ?? false,
            UrlBefore = beforeBrowser?.Url,
            UrlAfter = afterBrowser?.Url ?? stability?.UrlAfter,
            DocumentGenerationBefore = beforeBrowser?.DocumentGeneration,
            DocumentGenerationAfter = afterBrowser?.DocumentGeneration ?? stability?.DocumentGenerationAfter
        };
    }

    private static string ReadActionExecutionStatus(
        ActionExecutionResult? execution,
        ComputerUseException? failure)
    {
        if (execution is not null)
        {
            return execution.ActionExecuted ? "executed" : "not_executed";
        }

        return failure?.Details is JsonObject details
            && details["command_sent"] is JsonValue value
            && value.TryGetValue<bool>(out var commandSent)
            && commandSent
            ? "unknown"
            : "not_executed";
    }

    private static bool? ReadActionExecuted(JsonNode? details)
    {
        return details is JsonObject objectNode
            && objectNode["action_executed"] is JsonValue value
            && value.TryGetValue<bool>(out var actionExecuted)
            ? actionExecuted
            : null;
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
            ["interaction"] = JsonSerializer.SerializeToNode(result.Interaction),
            ["screenshot_hash"] = result.ScreenshotHash,
            ["capture"] = JsonSerializer.SerializeToNode(result.Capture),
            ["element_count"] = result.Elements.Count,
            ["ui_signature"] = observation.UiSignature
        };
    }

    private static JsonObject SessionSummary(
        WorkflowObservation? observation,
        ComputerUseException? failure)
    {
        if (observation?.Result.Browser is not { } browser)
        {
            return new JsonObject
            {
                ["surface"] = "desktop",
                ["valid"] = false
            };
        }

        var invalidCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "BROWSER_SESSION_CLOSED",
            "BROWSER_TARGET_CLOSED",
            "BROWSER_TARGET_NOT_FOUND",
            "BROWSER_BACKEND_UNAVAILABLE",
            "BROWSER_CAPTURE_UNAVAILABLE",
            "CDP_CONNECTION_FAILED"
        };
        return new JsonObject
        {
            ["surface"] = "browser",
            ["valid"] = failure is null || !invalidCodes.Contains(failure.Code),
            ["session_id"] = browser.SessionId,
            ["target_id"] = browser.TargetId,
            ["document_generation"] = browser.DocumentGeneration,
            ["url"] = browser.Url,
            ["title"] = browser.Title,
            ["lifecycle"] = browser.Lifecycle,
            ["navigation_complete"] = browser.NavigationComplete
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
