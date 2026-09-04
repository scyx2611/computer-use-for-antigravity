using System.Text.Json.Nodes;

namespace ComputerUse.Native.Tests;

internal static class WorkflowRunnerTests
{
    public static void Run()
    {
        RetrySucceedsOnSecondAttempt();
        RetryExhaustionIsStructured();
        StaleStateRecoversInsidePerform();
        AmbiguousTargetNeverBypassesSafety();
        ElevatedTargetIsNeverRetried();
        TraceOrderingAndFailureStepAreStable();
        RetryCountIsBounded();
    }

    private static void RetrySucceedsOnSecondAttempt()
    {
        var before = PostconditionTests.Observation("before", "Editor", "before", []);
        var wrong = PostconditionTests.Observation(
            "wrong",
            "Editor",
            "wrong",
            [PostconditionTests.Element(1, "Model", "Edit", "model", true, "Old")]);
        var correct = PostconditionTests.Observation(
            "correct",
            "Editor",
            "correct",
            [PostconditionTests.Element(1, "Model", "Edit", "model", true, "Gemini")]);
        var driver = new FakeDriver(
            [before, wrong, before, correct],
            (_, attempt) => Success("set_value", attempt));

        var result = new WorkflowRunner().Run(
            driver,
            [
                new JsonObject
                {
                    ["type"] = "set_value",
                    ["target"] = new JsonObject { ["automation_id"] = "model" },
                    ["value"] = "Gemini",
                    ["expect"] = new JsonObject
                    {
                        ["value"] = new JsonObject
                        {
                            ["target"] = new JsonObject { ["automation_id"] = "model" },
                            ["equals"] = "Gemini"
                        }
                    },
                    ["retry"] = new JsonObject { ["max_attempts"] = 2, ["delay_ms"] = 0 }
                }
            ],
            verify: true);

        TestAssert.Equal(2, driver.ExecuteCount, "postcondition retry should execute the idempotent action twice");
        TestAssert.Equal(2, result.ExecutionTrace.Count, "trace should contain both attempts");
        TestAssert.Equal("retrying", result.ExecutionTrace[0].FinalStatus, "first attempt should be marked retrying");
        TestAssert.Equal("passed", result.ExecutionTrace[1].FinalStatus, "second attempt should pass");
        TestAssert.Equal("postcondition_or_stability_retry", result.ExecutionTrace[0].RetryReason, "retry reason");
    }

    private static void RetryExhaustionIsStructured()
    {
        var observation = PostconditionTests.Observation(
            "state",
            "Editor",
            "hash",
            [PostconditionTests.Element(1, "Model", "Edit", "model", true, "Wrong")]);
        var driver = new FakeDriver(
            [observation],
            (_, attempt) => Success("set_value", attempt));

        var failure = TestAssert.Throws<ComputerUseException>(
            () => new WorkflowRunner().Run(
                driver,
                [ValueExpectation(maxAttempts: 2)],
                verify: true),
            "retry exhaustion should fail the workflow");

        TestAssert.Equal("POSTCONDITION_FAILED", failure.Code, "postcondition failure code");
        var details = failure.Details!.AsObject();
        TestAssert.Equal(1, details["step_index"]!.GetValue<int>(), "failed step index");
        TestAssert.Equal(2, details["attempts_used"]!.GetValue<int>(), "attempts used");
        TestAssert.True(details["retry_exhausted"]!.GetValue<bool>(), "retry exhaustion flag");
        TestAssert.Equal(2, details["execution_trace"]!.AsArray().Count, "failure trace attempt count");
        TestAssert.True(details["verification"] is not null, "failure should include verification details");
    }

    private static void StaleStateRecoversInsidePerform()
    {
        var first = PostconditionTests.Observation("first", "Editor", "first", []);
        var second = PostconditionTests.Observation("second", "Editor", "second", []);
        var driver = new FakeDriver(
            [first, second, second],
            (_, attempt) =>
            {
                if (attempt == 1)
                {
                    throw new ComputerUseException("STALE_STATE", "synthetic stale state");
                }

                return Success("click", attempt);
            });

        var result = new WorkflowRunner().Run(
            driver,
            [new JsonObject { ["type"] = "click", ["x"] = 1, ["y"] = 1 }],
            verify: true);

        TestAssert.Equal(2, driver.ExecuteCount, "perform should re-observe and retry stale state once");
        TestAssert.Equal("stale_state_reobserve", result.ExecutionTrace[0].RetryReason, "stale recovery reason");
        TestAssert.Equal("passed", result.ExecutionTrace[1].FinalStatus, "stale recovery final status");
    }

    private static void AmbiguousTargetNeverBypassesSafety()
    {
        var observation = PostconditionTests.Observation(
            "ambiguous",
            "Editor",
            "hash",
            [
                PostconditionTests.Element(1, "Save", "Button", "", true, null),
                PostconditionTests.Element(2, "Save", "Button", "", true, null)
            ]);
        var driver = new FakeDriver(
            [observation, observation, observation],
            (_, _) => throw new ComputerUseException(
                "AMBIGUOUS_TARGET",
                "synthetic ambiguity",
                new JsonObject { ["candidates"] = new JsonArray { 1, 2 } }));

        var failure = TestAssert.Throws<ComputerUseException>(
            () => new WorkflowRunner().Run(
                driver,
                [
                    new JsonObject
                    {
                        ["type"] = "click",
                        ["target"] = new JsonObject { ["name"] = "Save", ["role"] = "Button" },
                        ["retry"] = new JsonObject { ["max_attempts"] = 3, ["delay_ms"] = 0 }
                    }
                ],
                verify: true),
            "ambiguity must remain a hard failure");

        TestAssert.Equal("AMBIGUOUS_TARGET", failure.Code, "ambiguity code must be preserved");
        TestAssert.Equal(3, driver.ExecuteCount, "ambiguous target may only be re-observed within the bound");
        var details = failure.Details!.AsObject();
        TestAssert.True(details["cause_details"] is not null, "candidate details must be preserved");
        TestAssert.True(
            details["execution_trace"]!.AsArray().All(entry => !entry!["action_executed"]!.GetValue<bool>()),
            "ambiguous attempts must not report executed input");
    }

    private static void ElevatedTargetIsNeverRetried()
    {
        var ensureCalls = 0;
        var driver = new FakeDriver(
            [],
            (_, _) => Success("click", 1),
            () =>
            {
                ensureCalls++;
                throw new ComputerUseException("TARGET_ELEVATED", "synthetic elevated target");
            });

        var failure = TestAssert.Throws<ComputerUseException>(
            () => new WorkflowRunner().Run(
                driver,
                [
                    new JsonObject
                    {
                        ["type"] = "click",
                        ["x"] = 1,
                        ["y"] = 1,
                        ["retry"] = new JsonObject { ["max_attempts"] = 5, ["delay_ms"] = 0 }
                    }
                ],
                verify: true),
            "elevated target should fail without retry");

        TestAssert.Equal("TARGET_ELEVATED", failure.Code, "elevated error code");
        TestAssert.Equal(1, ensureCalls, "elevated target must be checked once");
        TestAssert.Equal(0, driver.ExecuteCount, "elevated target must not execute input");
    }

    private static void TraceOrderingAndFailureStepAreStable()
    {
        var firstBefore = PostconditionTests.Observation("1-before", "Editor", "1a", []);
        var firstAfter = PostconditionTests.Observation("1-after", "Editor", "1b", []);
        var secondBefore = PostconditionTests.Observation("2-before", "Editor", "2a", []);
        var secondAfter = PostconditionTests.Observation("2-after", "Editor", "2b", []);
        var driver = new FakeDriver(
            [firstBefore, firstAfter, secondBefore, secondAfter],
            (_, attempt) => Success("wait", attempt));

        var result = new WorkflowRunner().Run(
            driver,
            [
                new JsonObject { ["type"] = "wait", ["milliseconds"] = 0 },
                new JsonObject { ["type"] = "wait", ["milliseconds"] = 0 }
            ],
            verify: true);

        TestAssert.Equal(2, result.ExecutionTrace.Count, "successful workflow trace count");
        TestAssert.Equal(1, result.ExecutionTrace[0].StepIndex, "first trace step index");
        TestAssert.Equal(2, result.ExecutionTrace[1].StepIndex, "second trace step index");
        TestAssert.Equal("1-before", result.ExecutionTrace[0].StateIdBefore, "first before state");
        TestAssert.Equal("1-after", result.ExecutionTrace[0].StateIdAfter, "first after state");
        TestAssert.Equal("windows_graphics_capture", result.ExecutionTrace[0].CaptureBackend, "trace capture backend");
        TestAssert.True(result.ExecutionTrace[0].Resolution is not null, "trace should include target resolution");

        var failingDriver = new FakeDriver(
            [firstBefore, firstAfter, secondBefore, secondAfter],
            (_, attempt) =>
            {
                if (attempt == 2)
                {
                    return Success("set_value", attempt);
                }

                return Success("wait", attempt);
            });
        var failure = TestAssert.Throws<ComputerUseException>(
            () => new WorkflowRunner().Run(
                failingDriver,
                [
                    new JsonObject { ["type"] = "wait", ["milliseconds"] = 0 },
                    ValueExpectation(maxAttempts: 1)
                ],
                verify: true),
            "the second workflow step should report its own failure");
        TestAssert.Equal(2, failure.Details!.AsObject()["step_index"]!.GetValue<int>(), "structured failure step index");
    }

    private static void RetryCountIsBounded()
    {
        var observation = PostconditionTests.Observation(
            "bounded",
            "Editor",
            "hash",
            [PostconditionTests.Element(1, "Model", "Edit", "model", true, "Wrong")]);
        var driver = new FakeDriver(
            [observation],
            (_, attempt) => Success("set_value", attempt));

        var failure = TestAssert.Throws<ComputerUseException>(
            () => new WorkflowRunner().Run(
                driver,
                [ValueExpectation(maxAttempts: 5)],
                verify: true),
            "retry attempts must have a hard upper bound");

        TestAssert.Equal(5, driver.ExecuteCount, "retry policy must cap execution attempts");
        TestAssert.Equal(5, failure.Details!.AsObject()["execution_trace"]!.AsArray().Count, "bounded trace count");
    }

    private static JsonObject ValueExpectation(int maxAttempts)
    {
        return new JsonObject
        {
            ["type"] = "set_value",
            ["target"] = new JsonObject { ["automation_id"] = "model" },
            ["value"] = "Gemini",
            ["expect"] = new JsonObject
            {
                ["value"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["automation_id"] = "model" },
                    ["equals"] = "Gemini"
                }
            },
            ["retry"] = new JsonObject { ["max_attempts"] = maxAttempts, ["delay_ms"] = 0 }
        };
    }

    private static ActionExecutionResult Success(string type, int attempt)
    {
        return new ActionExecutionResult(
            new ActionRecord
            {
                Type = type,
                Success = true,
                Target = "synthetic target",
                Message = "synthetic action"
            },
            new JsonObject
            {
                ["method"] = "automation_id",
                ["element_id"] = attempt
            },
            true);
    }

    private sealed class FakeDriver : IWorkflowDriver
    {
        private readonly Queue<WorkflowObservation> observations;
        private readonly Func<WorkflowObservation, int, ActionExecutionResult> execute;
        private readonly Action ensureInputAllowed;
        private WorkflowObservation? lastObservation;

        public FakeDriver(
            IEnumerable<WorkflowObservation> observations,
            Func<WorkflowObservation, int, ActionExecutionResult> execute,
            Action? ensureInputAllowed = null)
        {
            this.observations = new Queue<WorkflowObservation>(observations);
            this.execute = execute;
            this.ensureInputAllowed = ensureInputAllowed ?? (() => { });
        }

        public int ExecuteCount { get; private set; }

        public void EnsureInputAllowed() => ensureInputAllowed();

        public WorkflowObservation Observe()
        {
            if (observations.Count > 0)
            {
                lastObservation = observations.Dequeue();
            }

            return lastObservation ?? throw new InvalidOperationException("fake observation queue is empty");
        }

        public ActionExecutionResult Execute(WorkflowObservation observation, JsonObject action)
        {
            ExecuteCount++;
            return execute(observation, ExecuteCount);
        }

        public UiStabilityResult WaitForUiStable(WorkflowObservation before)
        {
            return new UiStabilityResult
            {
                Status = "stable",
                Changed = true,
                InitialSignature = before.UiSignature,
                FinalSignature = before.UiSignature
            };
        }
    }
}
