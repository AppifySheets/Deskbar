using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Control;

// The `bookmark` command, parsed (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md). One request per
// subcommand, so RemoteControl.Bookmarks.cs only acts on a request that is already well formed.
public abstract record BookmarkRequest
{
    // `bookmark list [<workspace>]`: every workspace's bookmarks, or one workspace's.
    public sealed record List(string? Workspace) : BookmarkRequest;

    // `bookmark add`: Target is the url, the exe path or the app id according to Kind. Arguments and WorkingDirectory
    // only exist for a Program. Name is null when the caller gave none (the handler then names it after the target).
    public sealed record Add(string Workspace, BookmarkKind Kind, string Target, string? Arguments, string? WorkingDirectory, string? Name) : BookmarkRequest;

    // `bookmark rename`: the bookmark is named by its name or its id, as workspaces are.
    public sealed record Rename(string Workspace, string Bookmark, string Name) : BookmarkRequest;

    public sealed record Remove(string Workspace, string Bookmark) : BookmarkRequest;

    public sealed record Open(string Workspace, string Bookmark) : BookmarkRequest;

    public sealed record OpenAll(string Workspace) : BookmarkRequest;
}

public static class BookmarkArguments
{
    // Every failure of Parse is the command's own usage lines (all six), the way a wrong command prints its line.
    static Result<BookmarkRequest> Usage(string? why = null) =>
        Result.Failure<BookmarkRequest>(
            (why is null ? "" : why + "\n") + ControlUsage.LineFor("bookmark"));

    // `args` is everything after the verb `bookmark`.
    public static Result<BookmarkRequest> Parse(IReadOnlyList<string> args) => args switch
    {
        ["list"] => new BookmarkRequest.List(null),
        ["list", var workspace] => new BookmarkRequest.List(workspace),
        ["add", var workspace, ..] => ParseAdd(workspace, args.Skip(2).ToList()),
        ["rename", var workspace, var bookmark, "--name", var name] when !string.IsNullOrWhiteSpace(name) =>
            new BookmarkRequest.Rename(workspace, bookmark, name),
        ["remove", var workspace, var bookmark] => new BookmarkRequest.Remove(workspace, bookmark),
        ["open", var workspace, var bookmark] => new BookmarkRequest.Open(workspace, bookmark),
        ["open-all", var workspace] => new BookmarkRequest.OpenAll(workspace),
        _ => Usage(),
    };

    // `add <workspace> --url <url> | --program <path> [--cwd <folder>] [-- <args>] | --app <id> [--name <name>]`.
    // Exactly one of the three kinds. Everything after a `--` is the program's own arguments, taken verbatim so none
    // of them can be mistaken for an option, and only a program has any.
    static Result<BookmarkRequest> ParseAdd(string workspace, IReadOnlyList<string> rest)
    {
        var separator = rest.ToList().IndexOf("--");
        var options = separator < 0 ? rest : rest.Take(separator).ToList();
        var programArguments = separator < 0 ? null : rest.Skip(separator + 1).ToList();

        // Each option takes one value; a flag with no value, an unknown flag or a repeated one is a usage error.
        var pairs = options.Chunk(2).ToList();
        var wellFormed = pairs.All(p => p.Length == 2 && p[0] is "--url" or "--program" or "--app" or "--cwd" or "--name")
                         && pairs.Select(p => p[0]).Distinct().Count() == pairs.Count;
        if (!wellFormed) return Usage();

        var values = pairs.ToDictionary(p => p[0], p => p[1]);
        var kinds = new[] { "--url", "--program", "--app" }.Where(values.ContainsKey).ToList();
        if (kinds.Count != 1) return Usage();

        var kind = kinds[0] switch { "--url" => BookmarkKind.Link, "--program" => BookmarkKind.Program, _ => BookmarkKind.App };

        // --cwd and the arguments are a program's alone.
        if (kind != BookmarkKind.Program && (values.ContainsKey("--cwd") || programArguments is not null)) return Usage();

        var joined = programArguments is { Count: > 0 } ? Join(programArguments) : null;
        return new BookmarkRequest.Add(workspace, kind, values[kinds[0]], joined, values.GetValueOrDefault("--cwd"), values.GetValueOrDefault("--name"));
    }

    // Joins arguments into the one command-line string a Program bookmark stores (ruling P6), following the rules
    // CommandLineToArgvW and the C runtime read back: an argument is quoted when it is empty or holds a space, a tab
    // or a quote. Inside quotes a quote becomes \" and the backslashes just before it are doubled, and the backslashes
    // at the end of a quoted argument are doubled too (so they cannot escape the closing quote). An argument that
    // needs no quotes is written as it is, backslashes included.
    public static string Join(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')
            ? argument
            : QuoteAlways(argument);

    static string QuoteAlways(string argument)
    {
        // Backslashes are held back as a count until the next character shows whether a quote follows them.
        var (text, backslashes) = argument.Aggregate(
            (Text: new StringBuilder("\""), Backslashes: 0),
            (state, c) => c switch
            {
                '\\' => (state.Text, state.Backslashes + 1),
                '"' => (state.Text.Append('\\', state.Backslashes * 2).Append("\\\""), 0),
                _ => (state.Text.Append('\\', state.Backslashes).Append(c), 0),
            });
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
