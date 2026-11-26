using CSharpFunctionalExtensions;

namespace Deskbar.Core.Bookmarks;

// Turns what was dropped on a bookmark row into a bookmark, or says why not. Order, per the spec: a URL, then text
// that is a link, then the FIRST file only (a drop of five files is one bookmark attempt, not five).
public static class BookmarkDropParser
{
    const string Refusal = "Only links, programs and shortcuts can be bookmarked.";

    // The cheap answer for DragOver: no IO, so no file is opened while the pointer is still moving. It says yes for
    // anything Parse might accept and no, with the readout's reason, for what names alone rule out. A .lnk or .url
    // may still be refused by Parse once it is actually read.
    public static Result Precheck(BookmarkDrop drop) =>
        !string.IsNullOrWhiteSpace(drop.Url) ? (Result)BookmarkRules.Link(drop.Url)
        : !string.IsNullOrWhiteSpace(drop.Text) ? (Result)BookmarkRules.Link(drop.Text)
        : drop.Files.Count > 0 && IsSupportedFile(drop.Files[0]) ? Result.Success()
        : Result.Failure(Refusal);

    public static Result<Bookmark> Parse(BookmarkDrop drop, IBookmarkSources sources) =>
        !string.IsNullOrWhiteSpace(drop.Url) ? BookmarkRules.Link(drop.Url, TitleOf(drop.UrlTitle))
        : !string.IsNullOrWhiteSpace(drop.Text) ? BookmarkRules.Link(drop.Text)
        : drop.Files.Count > 0 ? FromFile(drop.Files[0], sources)
        : Result.Failure<Bookmark>(Refusal);

    static bool IsSupportedFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".url" or ".exe" or ".lnk";

    // "Portainer.url" -> "Portainer"; nothing usable -> null, so the link is named after its host.
    static string? TitleOf(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? null
            : (fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName).Trim() is { Length: > 0 } title
                ? title
                : null;

    static Result<Bookmark> FromFile(string path, IBookmarkSources sources) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".url" => sources.ReadText(path) is { } text && InternetShortcut.UrlOf(text).TryGetValue(out var url)
                ? BookmarkRules.Link(url, Path.GetFileNameWithoutExtension(path))
                : Result.Failure<Bookmark>("That internet shortcut has no address."),
            ".exe" => Program(path, null, null, sources.DescribeExe(path) ?? Path.GetFileNameWithoutExtension(path)),
            ".lnk" => sources.ResolveShortcut(path).TryGetValue(out var shortcut)
                ? FromShortcut(path, shortcut)
                : Result.Failure<Bookmark>("That shortcut could not be read."),
            _ => Result.Failure<Bookmark>(Refusal),
        };

    // A shortcut to a Store app has an id and no target; a shortcut to a program is named after the shortcut (the
    // name the user gave it). A shortcut to a document or folder opens nothing we can start.
    static Result<Bookmark> FromShortcut(string lnkPath, ShortcutTarget shortcut)
    {
        var name = Path.GetFileNameWithoutExtension(lnkPath);
        return string.IsNullOrWhiteSpace(shortcut.Target) && !string.IsNullOrWhiteSpace(shortcut.AppId)
            ? BookmarkRules.Check(new Bookmark(Guid.NewGuid(), name, BookmarkKind.App, shortcut.AppId))
            : shortcut.Target is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Program(target, NullIfBlank(shortcut.Arguments), NullIfBlank(shortcut.WorkingDirectory), name)
                : Result.Failure<Bookmark>(Refusal);
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static Result<Bookmark> Program(string exe, string? arguments, string? workingDirectory, string name) =>
        BookmarkRules.Check(new Bookmark(Guid.NewGuid(), name, BookmarkKind.Program, exe, arguments, workingDirectory));
}
