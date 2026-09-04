using System.IO;

namespace ComputerUse.Native;

internal sealed class BrowserProfileManager
{
    private const string ProductDirectoryName = "ComputerUseForAntigravity";
    private const string ProfilesDirectoryName = "browser-profiles";

    private readonly string profilesRoot;

    public BrowserProfileManager()
        : this(GetDefaultProfilesRoot())
    {
    }

    internal BrowserProfileManager(string profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot))
        {
            throw new ComputerUseException(
                "BROWSER_PROFILE_UNAVAILABLE",
                "The Windows LocalAppData directory is unavailable.");
        }

        this.profilesRoot = Path.GetFullPath(profilesRoot);
    }

    public string CreateEphemeral(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("The browser session id is not a safe directory name.", nameof(sessionId));
        }

        Directory.CreateDirectory(profilesRoot);
        var profilePath = Path.Combine(profilesRoot, $"session-{sessionId}");
        Directory.CreateDirectory(profilePath);
        return profilePath;
    }

    public void DeleteEphemeral(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(profilePath);
        if (!fullPath.StartsWith(profilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a browser profile outside the managed profile root.");
        }

        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: unable to delete browser profile '{fullPath}': {exception.Message}");
        }
    }

    private static string GetDefaultProfilesRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new ComputerUseException(
                "BROWSER_PROFILE_UNAVAILABLE",
                "The Windows LocalAppData directory is unavailable.");
        }

        return Path.Combine(localAppData, ProductDirectoryName, ProfilesDirectoryName);
    }
}
