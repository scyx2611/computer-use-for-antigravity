using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class ChromiumLauncher
{
    private readonly WindowManager windows;
    private readonly BrowserProfileManager profiles;

    public ChromiumLauncher(WindowManager windows, BrowserProfileManager profiles)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public ManagedBrowserSession Launch(
        string requestedPath,
        IReadOnlyList<string> userArguments,
        string? workingDirectory,
        bool waitForWindow,
        string? titleContains,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        var executable = ResolveExecutable(requestedPath, out var browserName);
        ValidateArguments(userArguments);

        var sessionId = Guid.NewGuid().ToString("N");
        var profilePath = profiles.CreateEphemeral(sessionId);
        Process? process = null;
        CdpEndpointClient? endpoint = null;
        ManagedBrowserSession? session = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
                    ?? Path.GetDirectoryName(executable)
                    ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            foreach (var argument in userArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo)
                ?? throw new ComputerUseException(
                    "LAUNCH_FAILED",
                    $"Unable to start managed {browserName}.");

            var port = WaitForDevToolsActivePort(
                process,
                profilePath,
                timeoutMilliseconds,
                pollMilliseconds);
            endpoint = new CdpEndpointClient(port);
            var target = WaitForPageTarget(
                process,
                endpoint,
                timeoutMilliseconds,
                pollMilliseconds);

            session = new ManagedBrowserSession(
                sessionId,
                browserName,
                executable,
                profilePath,
                process,
                endpoint,
                target,
                profiles);
            session.Connect(timeoutMilliseconds);

            if (waitForWindow)
            {
                var window = WaitForWindow(
                    process.Id,
                    titleContains,
                    timeoutMilliseconds,
                    pollMilliseconds);
                session.BindWindow(windows.ResolveWindowId(window.Id));
            }

            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                endpoint?.Dispose();
                StopProcess(process);
                profiles.DeleteEphemeral(profilePath);
            }

            throw;
        }
    }

    private static string ResolveExecutable(string requestedPath, out string browserName)
    {
        var requestedName = Path.GetFileName(requestedPath).ToLowerInvariant();
        browserName = requestedName switch
        {
            "chrome" or "chrome.exe" => "chrome",
            "msedge" or "msedge.exe" => "edge",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(browserName))
        {
            throw new ComputerUseException(
                "UNSUPPORTED_BROWSER",
                "Managed browser mode supports Google Chrome (chrome.exe) and Microsoft Edge (msedge.exe) only.",
                new JsonObject { ["path"] = requestedPath });
        }

        if (File.Exists(requestedPath))
        {
            return Path.GetFullPath(requestedPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
        var candidates = browserName == "chrome"
            ? new[]
            {
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
            }
            : new[]
            {
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe")
            };

        var resolved = candidates.FirstOrDefault(candidate => File.Exists(candidate));
        if (resolved is null)
        {
            throw new ComputerUseException(
                "BROWSER_EXECUTABLE_NOT_FOUND",
                $"Unable to find the managed {browserName} executable.",
                new JsonObject { ["path"] = requestedPath });
        }

        return resolved;
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--remote-debugging-port", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--remote-debugging-pipe", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase))
            {
                throw new ComputerUseException(
                    "INVALID_BROWSER_ARGUMENTS",
                    "Managed browser mode owns the debugging endpoint and user-data-dir flags.");
            }
        }

        BrowserUrlPolicy.ValidateInitialArguments(arguments);
    }

    private static int WaitForDevToolsActivePort(
        Process process,
        string profilePath,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        var portFile = Path.Combine(profilePath, "DevToolsActivePort");
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (process.HasExited)
            {
                throw new ComputerUseException(
                    "BROWSER_PROCESS_EXITED",
                    $"Managed browser exited before creating DevToolsActivePort (exit code {process.ExitCode}).");
            }

            try
            {
                if (File.Exists(portFile))
                {
                    var lines = File.ReadAllLines(portFile);
                    if (lines.Length > 0
                        && int.TryParse(lines[0], out var port)
                        && port is >= 1 and <= 65_535)
                    {
                        return port;
                    }
                }
            }
            catch (IOException)
            {
                // Chromium may still be replacing the file atomically.
            }
            catch (UnauthorizedAccessException)
            {
                // Retry until the bounded launch deadline.
            }

            Thread.Sleep(pollMilliseconds);
        }

        throw new ComputerUseException(
            "BROWSER_LAUNCH_TIMEOUT",
            $"Managed browser did not publish DevToolsActivePort within {timeoutMilliseconds} ms.",
            new JsonObject
            {
                ["profile"] = profilePath,
                ["timeout_ms"] = timeoutMilliseconds
            });
    }

    private static CdpTargetDescriptor WaitForPageTarget(
        Process process,
        CdpEndpointClient endpoint,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (process.HasExited)
            {
                throw new ComputerUseException(
                    "BROWSER_PROCESS_EXITED",
                    $"Managed browser exited before exposing a page target (exit code {process.ExitCode}).");
            }

            try
            {
                var target = endpoint.ListPageTargets()
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.WebSocketDebuggerUrl));
                if (target is not null)
                {
                    return target;
                }
            }
            catch (ComputerUseException exception) when (
                exception.Code is "BROWSER_BACKEND_UNAVAILABLE" or "CDP_TIMEOUT")
            {
                // The local endpoint can be published just before it starts
                // serving /json/list. Keep polling within the launch bound.
            }

            Thread.Sleep(pollMilliseconds);
        }

        throw new ComputerUseException(
            "BROWSER_TARGET_NOT_FOUND",
            $"Managed browser did not expose a page target within {timeoutMilliseconds} ms.");
    }

    private WindowInfo WaitForWindow(
        int processId,
        string? titleContains,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            foreach (var window in windows.ListVisibleWindows())
            {
                if (window.Pid != processId)
                {
                    continue;
                }

                if (titleContains is not null
                    && window.Title.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                return window;
            }

            Thread.Sleep(pollMilliseconds);
        }

        throw new ComputerUseException(
            "BROWSER_WINDOW_NOT_FOUND",
            $"Managed browser did not expose a visible window within {timeoutMilliseconds} ms.",
            new JsonObject
            {
                ["pid"] = processId,
                ["title_contains"] = titleContains
            });
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: unable to stop managed browser: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
