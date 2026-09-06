using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class BrowserTargetContext
{
    public BrowserTargetContext(
        ManagedBrowserSession session,
        CdpTargetDescriptor target,
        CdpConnection connection,
        IReadOnlyList<CdpTargetDescriptor> tabs)
    {
        Session = session;
        Target = target;
        Connection = connection;
        Tabs = tabs;
    }

    public ManagedBrowserSession Session { get; }

    public CdpTargetDescriptor Target { get; }

    public CdpConnection Connection { get; }

    public IReadOnlyList<CdpTargetDescriptor> Tabs { get; }
}

internal sealed class ManagedBrowserSession : IDisposable
{
    private readonly object sync = new();
    private readonly BrowserProfileManager profiles;
    private CdpConnection? connection;
    private CdpTargetDescriptor target;
    private string documentKey;
    private string documentGeneration = Guid.NewGuid().ToString("N");
    private int? documentNodeId;
    private string? loaderId;
    private bool disposed;

    public ManagedBrowserSession(
        string sessionId,
        string browserName,
        string executablePath,
        string profilePath,
        Process process,
        CdpEndpointClient endpoint,
        CdpTargetDescriptor target,
        BrowserProfileManager profiles)
    {
        SessionId = sessionId;
        BrowserName = browserName;
        ExecutablePath = executablePath;
        ProfilePath = profilePath;
        Process = process;
        Endpoint = endpoint;
        this.target = target;
        this.profiles = profiles;
        documentKey = BuildDocumentKey(target);
    }

    public string SessionId { get; }

    public string BrowserName { get; }

    public string ExecutablePath { get; }

    public string ProfilePath { get; }

    public Process Process { get; }

    public CdpEndpointClient Endpoint { get; }

    public IntPtr WindowHandle { get; private set; }

    public string TargetId => target.Id;

    public string DocumentGeneration
    {
        get
        {
            lock (sync)
            {
                return documentGeneration;
            }
        }
    }

    public string? LoaderId
    {
        get
        {
            lock (sync)
            {
                return loaderId;
            }
        }
    }

    public string DebugEndpoint => Endpoint.DebugEndpoint;

    public void Connect(int timeoutMilliseconds)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            connection = CdpConnection.Connect(target.WebSocketDebuggerUrl, timeoutMilliseconds);
        }
    }

    public void BindWindow(IntPtr hwnd)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            WindowHandle = hwnd;
        }
    }

    public BrowserTargetContext PrepareTarget(int timeoutMilliseconds)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (Process.HasExited)
            {
                throw new ComputerUseException(
                    "BROWSER_SESSION_CLOSED",
                    $"Managed {BrowserName} exited (exit code {Process.ExitCode}).",
                    new JsonObject
                    {
                        ["session_id"] = SessionId,
                        ["pid"] = Process.Id
                    });
            }

            var tabs = Endpoint.ListPageTargets(timeoutMilliseconds);
            if (tabs.Count == 0)
            {
                throw new ComputerUseException(
                    "BROWSER_TARGET_NOT_FOUND",
                    "The managed browser has no open page target.",
                    new JsonObject { ["session_id"] = SessionId });
            }

            var selected = tabs.FirstOrDefault(candidate => candidate.Id == target.Id)
                ?? tabs.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.WebSocketDebuggerUrl));
            if (selected is null || string.IsNullOrWhiteSpace(selected.WebSocketDebuggerUrl))
            {
                throw new ComputerUseException(
                    "BROWSER_TARGET_NOT_FOUND",
                    "The managed browser has no page target with a websocket endpoint.",
                    new JsonObject { ["session_id"] = SessionId });
            }

            if (connection is null
                || !connection.IsOpen
                || !string.Equals(selected.WebSocketDebuggerUrl, target.WebSocketDebuggerUrl, StringComparison.Ordinal))
            {
                connection?.Dispose();
                connection = CdpConnection.Connect(selected.WebSocketDebuggerUrl, timeoutMilliseconds);
            }

            target = selected;
            RefreshDocumentIdentity(selected);
            return new BrowserTargetContext(this, selected, connection, tabs);
        }
    }

    public void MarkNavigation(string? requestedUrl, string? requestedLoaderId)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            documentGeneration = Guid.NewGuid().ToString("N");
            documentNodeId = null;
            loaderId = requestedLoaderId;
            if (!string.IsNullOrWhiteSpace(requestedUrl))
            {
                documentKey = $"{target.Id}\u001f{requestedUrl}";
            }
        }
    }

    private void RefreshDocumentIdentity(CdpTargetDescriptor selected)
    {
        var nextKey = BuildDocumentKey(selected);
        if (!string.Equals(documentKey, nextKey, StringComparison.Ordinal))
        {
            documentGeneration = Guid.NewGuid().ToString("N");
            documentNodeId = null;
            loaderId = null;
            documentKey = nextKey;
        }
    }

    public void UpdateDocumentNode(int? nextDocumentNodeId)
    {
        if (nextDocumentNodeId is null)
        {
            return;
        }

        lock (sync)
        {
            ThrowIfDisposed();
            if (documentNodeId is not null && documentNodeId != nextDocumentNodeId)
            {
                documentGeneration = Guid.NewGuid().ToString("N");
                loaderId = null;
            }

            documentNodeId = nextDocumentNodeId;
        }
    }

    private static string BuildDocumentKey(CdpTargetDescriptor descriptor)
    {
        return $"{descriptor.Id}\u001f{descriptor.Url}";
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            connection?.Dispose();
            Endpoint.Dispose();

            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(3_000);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Computer Use for Antigravity: unable to stop managed {BrowserName}: {exception.Message}");
            }
            finally
            {
                Process.Dispose();
                profiles.DeleteEphemeral(ProfilePath);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ComputerUseException(
                "BROWSER_SESSION_CLOSED",
                "The managed browser session is closed.",
                new JsonObject { ["session_id"] = SessionId });
        }
    }
}

internal sealed class BrowserSessionManager : IDisposable
{
    private readonly WindowManager windows;
    private readonly ChromiumLauncher launcher;
    private readonly Dictionary<string, ManagedBrowserSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, ManagedBrowserSession> windowsToSessions = [];
    private bool disposed;

    public BrowserSessionManager(WindowManager windows)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
        launcher = new ChromiumLauncher(windows, new BrowserProfileManager());
    }

    public ManagedBrowserSession Launch(
        string path,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool waitForWindow,
        string? titleContains,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        if (disposed)
        {
            throw new ComputerUseException("BROWSER_SESSION_CLOSED", "The browser session manager is closed.");
        }

        var session = launcher.Launch(
            path,
            arguments,
            workingDirectory,
            waitForWindow,
            titleContains,
            timeoutMilliseconds,
            pollMilliseconds);
        sessions[session.SessionId] = session;
        if (session.WindowHandle != IntPtr.Zero)
        {
            windowsToSessions[session.WindowHandle] = session;
        }

        return session;
    }

    public bool TryGet(IntPtr hwnd, out ManagedBrowserSession? session)
    {
        if (windowsToSessions.TryGetValue(hwnd, out session))
        {
            return true;
        }

        var pid = windows.GetWindowInfo(hwnd).Pid;
        session = sessions.Values.FirstOrDefault(candidate =>
            !candidate.Process.HasExited && candidate.Process.Id == pid);
        if (session is null)
        {
            return false;
        }

        session.BindWindow(hwnd);
        windowsToSessions[hwnd] = session;
        return true;
    }

    public BrowserLaunchResult BuildLaunchResult(ManagedBrowserSession session)
    {
        WindowSnapshot? window = null;
        string? windowId = null;
        if (session.WindowHandle != IntPtr.Zero)
        {
            window = windows.GetWindowSnapshot(session.WindowHandle);
            windowId = window.Id;
        }

        return new BrowserLaunchResult
        {
            Path = session.ExecutablePath,
            Started = true,
            Pid = session.Process.Id,
            Window = window,
            Browser = new BrowserLaunchInfo
            {
                SessionId = session.SessionId,
                Browser = session.BrowserName,
                Managed = true,
                Profile = "ephemeral",
                TargetId = session.TargetId,
                WindowId = windowId,
                DebugEndpoint = session.DebugEndpoint
            }
        };
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var session in sessions.Values.ToArray())
        {
            session.Dispose();
        }

        sessions.Clear();
        windowsToSessions.Clear();
    }
}
