namespace ComputerUse.Native;

internal sealed class InteractionRoute
{
    public InteractionRoute(InteractionInfo interaction, ManagedBrowserSession? browserSession)
    {
        Interaction = interaction;
        BrowserSession = browserSession;
    }

    public InteractionInfo Interaction { get; }

    public ManagedBrowserSession? BrowserSession { get; }

    public bool IsManagedBrowser => BrowserSession is not null;
}

internal sealed class InteractionRouter
{
    private readonly WindowManager windows;
    private readonly BrowserSessionManager browsers;

    public InteractionRouter(WindowManager windows, BrowserSessionManager browsers)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
        this.browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
    }

    public InteractionRoute Resolve(IntPtr hwnd)
    {
        if (browsers.TryGet(hwnd, out var browser) && browser is not null)
        {
            return new InteractionRoute(
                new InteractionInfo
                {
                    Surface = "browser",
                    Backend = "browser_cdp"
                },
                browser);
        }

        var info = windows.GetWindowInfo(hwnd);
        var browserDetected = IsChromiumProcess(info.Process);
        return new InteractionRoute(
            new InteractionInfo
            {
                Surface = "desktop",
                Backend = "uia",
                BrowserDetected = browserDetected ? true : null,
                BrowserSemanticAvailable = browserDetected ? false : null
            },
            null);
    }

    private static bool IsChromiumProcess(string processName)
    {
        return string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase);
    }
}
