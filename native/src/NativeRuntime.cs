using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputerUse.Native.Rpc;

namespace ComputerUse.Native;

internal sealed class NativeRuntime : IDisposable
{
    private const int MaximumPerformActions = 64;
    private const int MaximumWaitMilliseconds = 60_000;

    private readonly WindowManager windows = new();
    private readonly AutomationEngine automation = new();
    private readonly CaptureEngine capture = new();
    private readonly InputEngine input = new();
    private readonly StateManager states = new();
    private readonly ActionExecutor actions;
    private readonly BrowserActionExecutor browserActions = new();
    private readonly BrowserSessionManager browsers;
    private readonly InteractionRouter router;
    private readonly BrowserObservationEngine browserObservation = new();

    public NativeRuntime()
    {
        actions = new ActionExecutor(automation, input, states, windows);
        browsers = new BrowserSessionManager(windows);
        router = new InteractionRouter(windows, browsers);
    }

    public RpcResponse Handle(RpcRequest request)
    {
        var result = request.Method switch
        {
            "ping" => Ping(),
            "list_windows" => ListWindows(),
            "observe" => Observe(request.Params),
            "act" => Act(request.Params),
            "perform" => Perform(request.Params),
            "wait_for" => WaitFor(request.Params),
            "launch" => Launch(request.Params),
            _ => throw new ComputerUseException("METHOD_NOT_FOUND", $"Unknown native method '{request.Method}'.")
        };

        return RpcResponse.Success(request.Id, result);
    }

    private static JsonNode Ping()
    {
        return new JsonObject
        {
            ["name"] = "computer-use-native",
            ["version"] = "0.4.0-phase2",
            ["platform"] = "windows",
            ["protocol"] = "jsonl"
        };
    }

    private JsonNode ListWindows()
    {
        return JsonSerializer.SerializeToNode(windows.ListVisibleWindows()) ?? new JsonArray();
    }

    private JsonNode Observe(JsonObject parameters)
    {
        var hwnd = windows.ResolveWindowId(GetOptionalString(parameters, "window_id"));
        var route = router.Resolve(hwnd);
        if (route.IsManagedBrowser && route.BrowserSession is not null)
        {
            return ToNode(ObserveBrowserWindow(hwnd, route.BrowserSession));
        }

        return ToNode(ObserveWindow(hwnd));
    }

    private JsonNode Act(JsonObject parameters)
    {
        var state = states.Require(GetOptionalString(parameters, "state_id"));
        if (parameters["window_id"] is not null)
        {
            var requestedWindow = windows.ResolveWindowId(GetOptionalString(parameters, "window_id"));
            if (requestedWindow != state.Hwnd)
            {
                throw new ComputerUseException(
                    "STATE_WINDOW_MISMATCH",
                    "window_id does not match the window captured by state_id.");
            }
        }

        var action = parameters["action"] as JsonObject
            ?? throw new ComputerUseException("ACTION_REQUIRED", "action must be a JSON object.");

        windows.EnsureInputAllowed(state.Hwnd);
        states.ValidateWindow(state, state.Hwnd, windows);

        var route = router.Resolve(state.Hwnd);
        if (route.IsManagedBrowser && route.BrowserSession is not null)
        {
            var targetContext = ValidateBrowserState(state, route.BrowserSession, state.Hwnd);
            var execution = browserActions.Execute(targetContext, state, action);
            return ToNode(execution.Record);
        }

        var desktopExecution = actions.Execute(state.Hwnd, state, action);
        return ToNode(desktopExecution.Record);
    }

    private JsonNode Perform(JsonObject parameters)
    {
        var hwnd = windows.ResolveWindowId(GetOptionalString(parameters, "window_id"));
        windows.EnsureInputAllowed(hwnd);

        var actionArray = parameters["actions"] as JsonArray
            ?? throw new ComputerUseException("ACTIONS_REQUIRED", "actions must be a JSON array.");
        if (actionArray.Count > MaximumPerformActions)
        {
            throw new ComputerUseException(
                "TOO_MANY_ACTIONS",
                $"perform accepts at most {MaximumPerformActions} actions per call.");
        }

        var verify = GetOptionalBool(parameters, "verify") ?? true;
        var actionObjects = new List<JsonObject>(actionArray.Count);
        foreach (var actionNode in actionArray)
        {
            if (actionNode is not JsonObject action)
            {
                throw new ComputerUseException("INVALID_ACTION", "Each item in actions must be a JSON object.");
            }

            actionObjects.Add(action);
        }

        var route = router.Resolve(hwnd);
        IWorkflowDriver driver;
        if (route.IsManagedBrowser && route.BrowserSession is not null)
        {
            var browserSession = route.BrowserSession;
            driver = new DelegateWorkflowDriver(
                ensureInputAllowed: () => windows.EnsureInputAllowed(hwnd),
                observe: () => ObserveBrowserWorkflow(hwnd, browserSession),
                execute: (observation, action) =>
                {
                    var state = states.Require(observation.Result.StateId);
                    states.ValidateWindow(state, hwnd, windows);
                    EnsureBrowserStateMatchesSession(state, browserSession);
                    return browserActions.Execute(browserSession.PrepareTarget(5_000), state, action);
                },
                waitForUiStable: observation => WaitForBrowserUiStable(hwnd, browserSession, observation.UiSignature));
        }
        else
        {
            driver = new DelegateWorkflowDriver(
                ensureInputAllowed: () => windows.EnsureInputAllowed(hwnd),
                observe: () => new WorkflowObservation(ObserveWindow(hwnd)),
                execute: (observation, action) =>
                {
                    var state = states.Require(observation.Result.StateId);
                    states.ValidateWindow(state, hwnd, windows);
                    return actions.Execute(hwnd, state, action);
                },
                waitForUiStable: observation => WaitForUiStable(hwnd, observation.UiSignature));
        }

        var workflow = new WorkflowRunner().Run(
            driver,
            actionObjects,
            verify);
        var finalObservation = workflow.FinalObservation?.Result;
        var window = windows.GetWindowSnapshot(hwnd);
        var interaction = route.Interaction;

        return ToNode(new PerformResult
        {
            Window = window,
            Interaction = interaction,
            Browser = finalObservation?.Browser,
            Actions = workflow.Actions,
            Status = "succeeded",
            ExecutionTrace = workflow.ExecutionTrace,
            Verified = workflow.Verified,
            StateId = finalObservation?.StateId,
            Screenshot = finalObservation?.Screenshot,
            ScreenshotHash = finalObservation?.ScreenshotHash,
            ScreenshotError = finalObservation?.ScreenshotError,
            Capture = finalObservation?.Capture,
            CoordinateSpaces = finalObservation?.CoordinateSpaces,
            Elements = finalObservation?.Elements,
            UiaError = finalObservation?.UiaError
        });
    }

    private JsonNode WaitFor(JsonObject parameters)
    {
        var requestedWindowId = GetOptionalString(parameters, "window_id");
        var titleContains = GetOptionalString(parameters, "title_contains");
        var processContains = GetOptionalString(parameters, "process_contains");
        var hasCondition = requestedWindowId is not null
            || titleContains is not null
            || processContains is not null;
        var timeoutMilliseconds = GetInt(parameters, "timeout_ms", hasCondition ? 5_000 : 250, 0, MaximumWaitMilliseconds);
        var pollMilliseconds = GetInt(parameters, "poll_ms", 100, 25, 2_000);
        var delayMilliseconds = GetInt(parameters, "milliseconds", 0, 0, MaximumWaitMilliseconds);
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);
        WindowInfo? matched = null;

        if (!hasCondition)
        {
            Thread.Sleep(delayMilliseconds);
            return new JsonObject
            {
                ["matched"] = true,
                ["elapsed_ms"] = ElapsedMilliseconds(start)
            };
        }

        while (true)
        {
            matched = FindWindow(requestedWindowId, titleContains, processContains);
            if (matched is not null)
            {
                break;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                break;
            }

            Thread.Sleep(pollMilliseconds);
        }

        var result = new JsonObject
        {
            ["matched"] = matched is not null,
            ["elapsed_ms"] = ElapsedMilliseconds(start)
        };
        if (matched is not null)
        {
            result["window"] = JsonSerializer.SerializeToNode(matched);
        }

        return result;
    }

    private JsonNode Launch(JsonObject parameters)
    {
        if (parameters["browser"] is not null)
        {
            return LaunchManagedBrowser(parameters);
        }

        var path = GetOptionalString(parameters, "path")
            ?? throw new ComputerUseException("PATH_REQUIRED", "path is required.");
        var workingDirectory = GetOptionalString(parameters, "working_directory");
        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            throw new ComputerUseException("WORKING_DIRECTORY_NOT_FOUND", $"Working directory '{workingDirectory}' does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };

        var arguments = parameters["args"];
        if (arguments is JsonValue argumentValue && argumentValue.TryGetValue<string>(out var argumentString))
        {
            startInfo.Arguments = argumentString;
        }
        else if (arguments is JsonArray argumentArray)
        {
            startInfo.Arguments = string.Join(' ', argumentArray.Select(argument =>
            {
                if (argument is not JsonValue value || !value.TryGetValue<string>(out var item))
                {
                    throw new ComputerUseException("INVALID_ARGS", "Each args array item must be a string.");
                }

                return QuoteWindowsArgument(item);
            }));
        }
        else if (arguments is not null)
        {
            throw new ComputerUseException("INVALID_ARGS", "args must be a string or an array of strings.");
        }

        try
        {
            using var process = Process.Start(startInfo);
            var result = new JsonObject
            {
                ["path"] = path,
                ["started"] = process is not null
            };
            if (process is not null)
            {
                result["pid"] = process.Id;
            }

            if (GetOptionalBool(parameters, "wait_for_window") == true)
            {
                var waitParameters = new JsonObject
                {
                    ["timeout_ms"] = GetInt(parameters, "timeout_ms", 5_000, 0, MaximumWaitMilliseconds),
                    ["poll_ms"] = GetInt(parameters, "poll_ms", 100, 25, 2_000)
                };
                var titleContains = GetOptionalString(parameters, "title_contains");
                if (titleContains is not null)
                {
                    waitParameters["title_contains"] = titleContains;
                }
                if (process is not null)
                {
                    waitParameters["process_contains"] = process.ProcessName;
                }

                result["window"] = WaitFor(waitParameters);
            }

            return result;
        }
        catch (ComputerUseException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ComputerUseException("LAUNCH_FAILED", $"Unable to launch '{path}': {exception.Message}");
        }
    }

    private JsonNode LaunchManagedBrowser(JsonObject parameters)
    {
        var browser = parameters["browser"] as JsonObject
            ?? throw new ComputerUseException("INVALID_PARAMS", "browser must be a JSON object.");
        var mode = GetOptionalString(browser, "mode") ?? "managed";
        if (!string.Equals(mode, "managed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ComputerUseException(
                "BROWSER_MODE_UNSUPPORTED",
                "Only browser.mode='managed' is supported; attaching an existing browser is intentionally unavailable.");
        }

        var profile = GetOptionalString(browser, "profile") ?? "ephemeral";
        if (!string.Equals(profile, "ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            throw new ComputerUseException(
                "BROWSER_PROFILE_UNSUPPORTED",
                "Managed browser mode only supports an isolated ephemeral profile.");
        }

        var path = GetOptionalString(parameters, "path")
            ?? throw new ComputerUseException("PATH_REQUIRED", "path is required for managed Chrome or Edge.");
        var workingDirectory = GetOptionalString(parameters, "working_directory");
        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            throw new ComputerUseException("WORKING_DIRECTORY_NOT_FOUND", $"Working directory '{workingDirectory}' does not exist.");
        }

        var arguments = ReadManagedArguments(parameters["args"]);
        var waitForWindow = GetOptionalBool(parameters, "wait_for_window") ?? true;
        var timeoutMilliseconds = GetInt(parameters, "timeout_ms", 15_000, 1_000, MaximumWaitMilliseconds);
        var pollMilliseconds = GetInt(parameters, "poll_ms", 100, 25, 2_000);
        var titleContains = GetOptionalString(parameters, "title_contains");
        var session = browsers.Launch(
            path,
            arguments,
            workingDirectory,
            waitForWindow,
            titleContains,
            timeoutMilliseconds,
            pollMilliseconds);
        return ToNode(browsers.BuildLaunchResult(session));
    }

    private ObserveResult ObserveWindow(IntPtr hwnd)
    {
        windows.EnsureWindowExists(hwnd);
        var rect = windows.GetWindowRectData(hwnd);
        var info = windows.GetWindowInfo(hwnd);
        IReadOnlyList<AutomationElementInfo> infos = [];
        string? uiaError = null;

        try
        {
            infos = automation.GetElementInfos(hwnd);
        }
        catch (ComputerUseException exception) when (exception.Code == "UIA_UNAVAILABLE")
        {
            uiaError = exception.Message;
        }

        var elements = infos.Select(infoItem => infoItem.Snapshot).ToArray();
        var screenshot = capture.Capture(hwnd, rect);
        var state = states.Create(hwnd, rect, info.Title, screenshot.Hash, elements);

        return new ObserveResult
        {
            StateId = state.StateId,
            Window = windows.GetWindowSnapshot(hwnd, rect),
            Interaction = new InteractionInfo
            {
                Surface = "desktop",
                Backend = "uia",
                BrowserDetected = IsChromiumProcess(info.Process) ? true : null,
                BrowserSemanticAvailable = IsChromiumProcess(info.Process) ? false : null
            },
            Elements = elements,
            Screenshot = screenshot.Base64,
            ScreenshotHash = screenshot.Hash,
            ScreenshotError = screenshot.Error,
            Capture = screenshot.Diagnostics,
            CoordinateSpaces = new CoordinateSpaces(),
            UiaError = uiaError
        };
    }

    private ObserveResult ObserveBrowserWindow(IntPtr hwnd, ManagedBrowserSession session)
    {
        windows.EnsureWindowExists(hwnd);
        var rect = windows.GetWindowRectData(hwnd);
        var window = windows.GetWindowSnapshot(hwnd, rect);
        var browser = browserObservation.Observe(session, hwnd, rect, window, capture);
        var state = states.Create(
            hwnd,
            rect,
            window.Title,
            browser.Screenshot.Hash,
            browser.Elements,
            new BrowserStateMetadata
            {
                SessionId = browser.Browser.SessionId,
                TargetId = browser.Browser.TargetId,
                FrameId = browser.FrameId,
                LoaderId = browser.LoaderId,
                DocumentGeneration = browser.DocumentGeneration,
                Url = browser.Browser.Url,
                Title = browser.Browser.Title,
                DocumentNodeId = browser.DocumentNodeId,
                SemanticSignature = browser.SemanticSignature,
                ElementHandles = browser.ElementHandles
            });

        return new ObserveResult
        {
            StateId = state.StateId,
            Window = window,
            Interaction = new InteractionInfo
            {
                Surface = "browser",
                Backend = "browser_cdp"
            },
            Browser = browser.Browser,
            Elements = browser.Elements,
            Screenshot = browser.Screenshot.Base64,
            ScreenshotHash = browser.Screenshot.Hash,
            ScreenshotError = browser.Screenshot.Error,
            Capture = browser.Screenshot.Diagnostics,
            CoordinateSpaces = new CoordinateSpaces
            {
                Screenshot = browser.ScreenshotCoordinateSpace,
                Elements = "viewport"
            }
        };
    }

    private WorkflowObservation ObserveBrowserWorkflow(
        IntPtr hwnd,
        ManagedBrowserSession session)
    {
        var result = ObserveBrowserWindow(hwnd, session);
        var state = states.Require(result.StateId);
        return new WorkflowObservation(
            result,
            state.Browser?.SemanticSignature);
    }

    private BrowserTargetContext ValidateBrowserState(
        WindowState state,
        ManagedBrowserSession session,
        IntPtr hwnd)
    {
        if (state.Browser is null)
        {
            throw new ComputerUseException(
                "STALE_BROWSER_STATE",
                "The requested state is not a managed browser observation.");
        }

        var current = ObserveBrowserWindow(hwnd, session);
        var currentState = states.Require(current.StateId);
        EnsureBrowserStateMatches(state.Browser, currentState.Browser);
        return session.PrepareTarget(5_000);
    }

    private static void EnsureBrowserStateMatchesSession(
        WindowState state,
        ManagedBrowserSession session)
    {
        if (state.Browser is null)
        {
            throw new ComputerUseException(
                "STALE_BROWSER_STATE",
                "The workflow observation is not a managed browser state.");
        }

        if (!string.Equals(state.Browser.SessionId, session.SessionId, StringComparison.Ordinal)
            || !string.Equals(state.Browser.TargetId, session.TargetId, StringComparison.Ordinal)
            || !string.Equals(state.Browser.DocumentGeneration, session.DocumentGeneration, StringComparison.Ordinal))
        {
            throw new ComputerUseException(
                "STALE_BROWSER_STATE",
                "The browser document changed after the workflow observation; re-observe before acting.",
                new JsonObject
                {
                    ["state_id"] = state.StateId,
                    ["state_document_generation"] = state.Browser.DocumentGeneration,
                    ["current_document_generation"] = session.DocumentGeneration
                });
        }
    }

    private static void EnsureBrowserStateMatches(
        BrowserStateMetadata expected,
        BrowserStateMetadata? actual)
    {
        if (actual is null
            || !string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
            || !string.Equals(expected.TargetId, actual.TargetId, StringComparison.Ordinal)
            || !string.Equals(expected.DocumentGeneration, actual.DocumentGeneration, StringComparison.Ordinal)
            || !string.Equals(expected.SemanticSignature, actual.SemanticSignature, StringComparison.Ordinal))
        {
            throw new ComputerUseException(
                "STALE_BROWSER_STATE",
                "The browser document or semantic UI changed; observe again before acting.",
                new JsonObject
                {
                    ["state_document_generation"] = expected.DocumentGeneration,
                    ["current_document_generation"] = actual?.DocumentGeneration,
                    ["state_url"] = expected.Url,
                    ["current_url"] = actual?.Url,
                    ["state_semantic_signature"] = expected.SemanticSignature,
                    ["current_semantic_signature"] = actual?.SemanticSignature
                });
        }
    }

    private UiStabilityResult WaitForBrowserUiStable(
        IntPtr hwnd,
        ManagedBrowserSession session,
        string initialSignature)
    {
        const int timeoutMilliseconds = 2_500;
        const int pollMilliseconds = 100;
        const int stableSamplesRequired = 2;
        var start = Stopwatch.GetTimestamp();
        var deadline = start
            + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);
        string? previous = null;
        string? finalSignature = null;
        var stableSamples = 0;
        var changed = false;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                var current = ObserveBrowserWorkflow(hwnd, session);
                finalSignature = current.UiSignature;
                changed |= !string.Equals(initialSignature, current.UiSignature, StringComparison.Ordinal);
                if (string.Equals(previous, current.UiSignature, StringComparison.Ordinal))
                {
                    stableSamples++;
                }
                else
                {
                    stableSamples = 1;
                    previous = current.UiSignature;
                }

                if (stableSamples >= stableSamplesRequired)
                {
                    return new UiStabilityResult
                    {
                        Status = "stable",
                        Changed = changed,
                        ElapsedMilliseconds = ElapsedMilliseconds(start),
                        InitialSignature = initialSignature,
                        FinalSignature = finalSignature
                    };
                }
            }
            catch (ComputerUseException exception) when (
                exception.Code is "STALE_BROWSER_STATE"
                    or "BROWSER_FRAME_DETACHED"
                    or "CDP_TIMEOUT")
            {
                // Navigation and document replacement can briefly invalidate
                // one observation. Keep polling within the bounded wait.
            }

            Thread.Sleep(pollMilliseconds);
        }

        return new UiStabilityResult
        {
            Status = "timeout",
            Changed = changed,
            ElapsedMilliseconds = ElapsedMilliseconds(start),
            InitialSignature = initialSignature,
            FinalSignature = finalSignature
        };
    }

    private UiStabilityResult WaitForUiStable(IntPtr hwnd, string initialSignature)
    {
        const int timeoutMilliseconds = 1_500;
        const int pollMilliseconds = 100;
        const int stableSamplesRequired = 2;
        var deadline = Stopwatch.GetTimestamp()
            + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);
        string? previous = null;
        string? finalSignature = null;
        var stableSamples = 0;
        var changed = false;
        var start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetTimestamp() < deadline)
        {
            string? current = null;
            try
            {
                current = automation.GetUiSignature(hwnd);
            }
            catch (ComputerUseException)
            {
                // Some providers briefly reject queries while they redraw.
            }

            if (current is not null)
            {
                finalSignature = current;
                changed |= !string.Equals(initialSignature, current, StringComparison.Ordinal);
                if (string.Equals(previous, current, StringComparison.Ordinal))
                {
                    stableSamples++;
                }
                else
                {
                    stableSamples = 1;
                    previous = current;
                }

                if (stableSamples >= stableSamplesRequired)
                {
                    return new UiStabilityResult
                    {
                        Status = "stable",
                        Changed = changed,
                        ElapsedMilliseconds = ElapsedMilliseconds(start),
                        InitialSignature = initialSignature,
                        FinalSignature = finalSignature
                    };
                }
            }

            Thread.Sleep(pollMilliseconds);
        }

        return new UiStabilityResult
        {
            Status = "timeout",
            Changed = changed,
            ElapsedMilliseconds = ElapsedMilliseconds(start),
            InitialSignature = initialSignature,
            FinalSignature = finalSignature
        };
    }

    private WindowInfo? FindWindow(string? windowId, string? titleContains, string? processContains)
    {
        foreach (var window in windows.ListVisibleWindows())
        {
            if (windowId is not null && !string.Equals(window.Id, windowId, StringComparison.Ordinal))
            {
                continue;
            }

            if (titleContains is not null
                && window.Title.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (processContains is not null
                && window.Process.IndexOf(processContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            return window;
        }

        return null;
    }

    private static JsonNode ToNode<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value) ?? new JsonObject();
    }

    private static string? GetOptionalString(JsonObject parameters, string name)
    {
        var node = parameters[name];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var result))
        {
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        throw new ComputerUseException("INVALID_PARAMS", $"{name} must be a string.");
    }

    private static IReadOnlyList<string> ReadManagedArguments(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var argumentString))
        {
            return SplitWindowsArguments(argumentString);
        }

        if (node is JsonArray argumentArray)
        {
            var result = new List<string>(argumentArray.Count);
            foreach (var argument in argumentArray)
            {
                if (argument is not JsonValue itemValue || !itemValue.TryGetValue<string>(out var item))
                {
                    throw new ComputerUseException("INVALID_ARGS", "Each args array item must be a string.");
                }

                result.Add(item);
            }

            return result;
        }

        throw new ComputerUseException("INVALID_ARGS", "args must be a string or an array of strings.");
    }

    private static IReadOnlyList<string> SplitWindowsArguments(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static bool IsChromiumProcess(string processName)
    {
        return string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? GetOptionalBool(JsonObject parameters, string name)
    {
        var node = parameters[name];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new ComputerUseException("INVALID_PARAMS", $"{name} must be a boolean.");
    }

    private static int GetInt(JsonObject parameters, string name, int defaultValue, int minimum, int maximum)
    {
        var node = parameters[name];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var result))
        {
            throw new ComputerUseException("INVALID_PARAMS", $"{name} must be an integer.");
        }

        if (result < minimum || result > maximum)
        {
            throw new ComputerUseException("INVALID_PARAMS", $"{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static long ElapsedMilliseconds(long startTimestamp)
    {
        return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0
            && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    public void Dispose()
    {
        browsers.Dispose();
    }
}
