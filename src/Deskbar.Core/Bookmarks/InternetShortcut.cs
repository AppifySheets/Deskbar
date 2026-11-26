using CSharpFunctionalExtensions;

namespace Deskbar.Core.Bookmarks;

// A .url file is an ini file: "[InternetShortcut]" then "URL=https://...". Only the address matters here.
public static class InternetShortcut
{
    // The URL= value of the [InternetShortcut] section, or none. Section and key names are case-insensitive, as
    // Windows reads them; other sections (a .url can carry icon data sections) are skipped.
    public static Maybe<string> UrlOf(string? iniText)
    {
        var inSection = false;
        var lines = (iniText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
        return lines
            .Where(line =>
            {
                if (line.StartsWith('['))
                {
                    inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    return false;
                }
                return inSection && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase);
            })
            .Select(line => line[4..].Trim())
            .Where(url => url.Length > 0)
            .Select(url => Maybe.From(url))
            .FirstOrDefault(Maybe<string>.None);
    }
}
