using System.Text.Json.Nodes;

namespace ComputerUse.Native.Tests;

internal static class PostconditionTests
{
    public static void Run()
    {
        var before = Observation(
            "before",
            "Preferences",
            "before-hash",
            [
                Element(1, "Settings", "Button", "settingsButton", true, null),
                Element(2, "Advanced", "Button", "advancedButton", false, null)
            ]);
        var after = Observation(
            "after",
            "Preferences - Settings",
            "after-hash",
            [
                Element(1, "Settings", "Window", "settingsWindow", true, null),
                Element(2, "Advanced", "Button", "advancedButton", false, null),
                Element(3, "Model", "Edit", "modelSelector", true, "Gemini")
            ]);
        var stable = new UiStabilityResult
        {
            Status = "stable",
            Changed = true
        };

        var successful = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["element"] = new JsonObject { ["name"] = "Settings", ["role"] = "Window" },
                ["element_enabled"] = new JsonObject { ["automation_id"] = "modelSelector" },
                ["value"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["automation_id"] = "modelSelector" },
                    ["equals"] = "Gemini"
                },
                ["window_title_contains"] = "Settings",
                ["ui_changed"] = true,
                ["ui_stable"] = true
            },
            before,
            after,
            stable);
        TestAssert.True(successful.Passed, "all deterministic postconditions should pass");
        TestAssert.Equal(6, successful.Checks.Count, "postcondition check count");

        var absent = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["element_absent"] = new JsonObject { ["name"] = "Missing", ["role"] = "Button" }
            },
            before,
            after,
            stable);
        TestAssert.True(absent.Passed, "element_absent should pass when no element matches");

        var disabled = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["element_disabled"] = new JsonObject { ["automation_id"] = "advancedButton" }
            },
            before,
            after,
            stable);
        TestAssert.True(disabled.Passed, "element_disabled should inspect UIA enabled state");

        var failed = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["value"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["automation_id"] = "modelSelector" },
                    ["equals"] = "Other"
                }
            },
            before,
            after,
            stable);
        TestAssert.True(!failed.Passed, "a mismatched value must fail");
        TestAssert.Equal("POSTCONDITION_FAILED", failed.ErrorCode, "failed postcondition error code");
        TestAssert.Equal("value_equals", failed.Checks[0].Type, "failed value check type");

        var notStable = PostconditionEvaluator.Evaluate(
            new JsonObject { ["ui_stable"] = false },
            before,
            after,
            new UiStabilityResult { Status = "timeout" });
        TestAssert.True(notStable.Passed, "ui_stable=false should match a bounded timeout result");

        var invalid = TestAssert.Throws<ComputerUseException>(
            () => PostconditionEvaluator.Validate(
                new JsonObject
                {
                    ["element"] = new JsonObject
                    {
                        ["x"] = 0.5,
                        ["y"] = 0.5,
                        ["coordinate_space"] = "normalized"
                    }
                }),
            "element postconditions must reject coordinate targets");
        TestAssert.Equal("INVALID_POSTCONDITION", invalid.Code, "invalid postcondition error code");

        BrowserConditionsUseUrlTextAndNavigationState();
    }

    internal static WorkflowObservation Observation(
        string stateId,
        string title,
        string screenshotHash,
        IReadOnlyList<UiElementSnapshot> elements,
        BrowserInfo? browser = null,
        string? uiSignature = null,
        InteractionInfo? interaction = null,
        string captureBackend = "windows_graphics_capture")
    {
        return new WorkflowObservation(
            new ObserveResult
            {
                StateId = stateId,
                Window = new WindowSnapshot
                {
                    Id = "123",
                    Title = title,
                    X = 10,
                    Y = 20,
                    Width = 500,
                    Height = 300,
                    Pid = 99
                },
                Interaction = interaction ?? new InteractionInfo
                {
                    Surface = browser is null ? "desktop" : "browser",
                    Backend = browser is null ? "uia" : "browser_cdp"
                },
                Browser = browser,
                Elements = elements,
                ScreenshotHash = screenshotHash,
                Capture = new CaptureDiagnostics
                {
                    Backend = captureBackend,
                    Width = 500,
                    Height = 300
                }
            },
            uiSignature ?? stateId);
    }

    internal static UiElementSnapshot Element(
        int id,
        string name,
        string role,
        string automationId,
        bool enabled,
        string? value)
    {
        return new UiElementSnapshot
        {
            Id = id,
            Name = name,
            Role = role,
            AutomationId = automationId,
            Bounds = [id * 10, id * 10, 100, 30],
            IsEnabled = enabled,
            RuntimeId = $"runtime-{id}",
            Value = value,
            Text = name
        };
    }

    private static void BrowserConditionsUseUrlTextAndNavigationState()
    {
        var before = Observation(
            "browser-before",
            "Computer Use form",
            "before-hash",
            [
                Element(1, "Name", "textbox", "name", true, null),
                Element(2, "Submit", "button", "submit", true, null)
            ],
            browser: Browser("http://127.0.0.1:54123/form.html", "Computer Use form", "document-a"),
            uiSignature: "semantic-a");
        var after = Observation(
            "browser-after",
            "Computer Use success",
            "after-hash",
            [
                Element(1, "Success", "heading", "", true, null),
                Element(2, "Name", "textbox", "name", true, "Antigravity"),
                Element(3, "Disabled", "button", "disabled", false, null)
            ],
            browser: Browser(
                "http://127.0.0.1:54123/success.html?name=Antigravity",
                "Computer Use success",
                "document-b"),
            uiSignature: "semantic-b",
            captureBackend: "cdp_page_capture");
        var stable = new UiStabilityResult
        {
            Status = "stable",
            Changed = true,
            SampleCount = 3,
            QuietSamples = 3,
            NavigationOccurred = true,
            NavigationComplete = true
        };

        var verification = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["url_equals"] = "http://127.0.0.1:54123/success.html?name=Antigravity",
                ["url_contains"] = "/success.html",
                ["title_contains"] = "success",
                ["element_exists"] = new JsonObject { ["role"] = "heading", ["name"] = "Success" },
                ["element_absent"] = new JsonObject { ["role"] = "button", ["name"] = "Submit" },
                ["element_enabled"] = new JsonObject { ["role"] = "textbox", ["name"] = "Name" },
                ["element_disabled"] = new JsonObject { ["role"] = "button", ["name"] = "Disabled" },
                ["value_equals"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["role"] = "textbox", ["name"] = "Name" },
                    ["equals"] = "Antigravity"
                },
                ["text_equals"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["role"] = "heading", ["name"] = "Success" },
                    ["equals"] = "Success"
                },
                ["text_contains"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["role"] = "heading", ["name"] = "Success" },
                    ["contains"] = "ucc"
                },
                ["text"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["role"] = "heading", ["name"] = "Success" },
                    ["equals"] = "Success"
                },
                ["page_changed"] = true,
                ["page_stable"] = true,
                ["navigation_complete"] = true
            },
            before,
            after,
            stable);

        TestAssert.True(verification.Passed, "browser URL, text, value, and navigation postconditions should pass");
        TestAssert.Equal(14, verification.Checks.Count, "browser postcondition check count");

        var failed = PostconditionEvaluator.Evaluate(
            new JsonObject
            {
                ["url_contains"] = "/missing",
                ["navigation_complete"] = false
            },
            before,
            after,
            stable);
        TestAssert.True(!failed.Passed, "browser postcondition mismatch must fail");
        TestAssert.Equal("POSTCONDITION_FAILED", failed.ErrorCode, "browser mismatch error code");
    }

    internal static BrowserInfo Browser(string url, string title, string documentGeneration)
    {
        return new BrowserInfo
        {
            SessionId = "session",
            Browser = "chrome",
            TargetId = "target",
            Url = url,
            Title = title,
            DocumentGeneration = documentGeneration,
            Lifecycle = "stable",
            NavigationComplete = true
        };
    }
}
