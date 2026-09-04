namespace ComputerUse.Native;

internal static class BrowserUrlPolicy
{
    public static void ValidateInitialArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (LooksLikeUrl(argument))
            {
                Validate(argument);
            }
        }
    }

    public static void Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            throw new ComputerUseException(
                "UNSUPPORTED_URL_SCHEME",
                $"Browser URL '{url}' is not an absolute URL.");
        }

        if (string.Equals(parsed.Scheme, "about", StringComparison.OrdinalIgnoreCase)
            && string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(parsed.Host))
            {
                throw new ComputerUseException(
                    "UNSUPPORTED_URL_SCHEME",
                    $"Browser URL '{url}' has no host.");
            }

            return;
        }

        throw new ComputerUseException(
            "UNSUPPORTED_URL_SCHEME",
            $"Browser URL scheme '{parsed.Scheme}' is not allowed. Use http, https, or about:blank.",
            new System.Text.Json.Nodes.JsonObject
            {
                ["url"] = url,
                ["scheme"] = parsed.Scheme
            });
    }

    private static bool LooksLikeUrl(string argument)
    {
        return Uri.TryCreate(argument, UriKind.Absolute, out var parsed)
            && !string.IsNullOrWhiteSpace(parsed.Scheme);
    }
}
