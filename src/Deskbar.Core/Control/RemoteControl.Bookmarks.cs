using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Control;

// The bookmark chip menu and the row menu's bookmark items (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md).
// See RemoteControl.cs for the shared parts. Every subcommand calls the same manager or opener method the bar's menu
// calls, so a remote add obeys the rules a drop does (BookmarkRules, the duplicate refusal) and opening goes through
// the one BookmarkOpener the chips use.
public sealed partial class RemoteControl
{
    const string NoOpener = "This copy of Deskbar cannot open bookmarks.";

    ControlReply BookmarkCommand(IReadOnlyList<string> args) =>
        BookmarkArguments.Parse(args).Match(RunBookmark, ControlReply.Fail);

    ControlReply RunBookmark(BookmarkRequest request) => request switch
    {
        BookmarkRequest.List list => BookmarkList(list.Workspace),
        BookmarkRequest.Add add => BookmarkAdd(add),
        BookmarkRequest.Rename rename => BookmarkRename(rename),
        BookmarkRequest.Remove remove => BookmarkRemove(remove),
        BookmarkRequest.Open open => BookmarkOpen(open),
        BookmarkRequest.OpenAll openAll => BookmarkOpenAll(openAll),
        _ => ControlReply.Fail(ControlUsage.LineFor("bookmark")),
    };

    // One entry per bookmark, flat, so a caller can filter by `workspace`. `running` comes from the opener (the same
    // answer that hides a chip); without one nothing is known to run.
    ControlReply BookmarkList(string? workspace) =>
        (workspace is null ? Result.Success(manager.State.Workspaces) : WorkspaceNamed(workspace).Map(w => (IReadOnlyList<Workspace>)[w]))
            .Match(
                workspaces => Success(workspaces.SelectMany(w => w.Bookmarks.Select(b => DescribeBookmark(w, b))).ToList()),
                ControlReply.Fail);

    object DescribeBookmark(Workspace workspace, Bookmark bookmark) => new
    {
        workspace = workspace.Name,
        id = bookmark.Id,
        name = bookmark.Name,
        kind = bookmark.Kind.ToString().ToLowerInvariant(),
        target = bookmark.Target,
        arguments = bookmark.Arguments,
        workingDirectory = bookmark.WorkingDirectory,
        running = bookmarks?.IsRunning(bookmark) ?? false,
    };

    // The twin of the row menu's "Bookmark link from clipboard" (--url, with the link typed instead of pasted), of a
    // drop (--program, --url) and of a window's "Bookmark this" (--program, --app).
    ControlReply BookmarkAdd(BookmarkRequest.Add add) =>
        WorkspaceNamed(add.Workspace)
            .Bind(w => NewBookmark(add).Bind(b => manager.AddBookmark(w.Id, b)).Map(b => DescribeBookmark(w, b)))
            .Match(Success, ControlReply.Fail);

    // A bookmark with no name given is named after what it opens: the link's host, the program's file name, the app id.
    Result<Bookmark> NewBookmark(BookmarkRequest.Add add) => add.Kind switch
    {
        BookmarkKind.Link => BookmarkRules.Link(add.Target, add.Name),
        BookmarkKind.Program => Result.Success(new Bookmark(Guid.NewGuid(),
            string.IsNullOrWhiteSpace(add.Name) ? Path.GetFileNameWithoutExtension(add.Target) : add.Name,
            BookmarkKind.Program, add.Target, add.Arguments, Folder(add.WorkingDirectory))),
        _ => Result.Success(new Bookmark(Guid.NewGuid(), string.IsNullOrWhiteSpace(add.Name) ? add.Target : add.Name, BookmarkKind.App, add.Target)),
    };

    ControlReply BookmarkRename(BookmarkRequest.Rename rename) =>
        WorkspaceNamed(rename.Workspace)
            .Bind(w => BookmarkNamed(w, rename.Bookmark).Bind(b => manager.RenameBookmark(w.Id, b.Id, rename.Name).Map(() => (w, b.Id))))
            .Match(found => Success(DescribeBookmark(found.w, BookmarkById(found.w.Id, found.Id))), ControlReply.Fail);

    ControlReply BookmarkRemove(BookmarkRequest.Remove remove) =>
        WorkspaceNamed(remove.Workspace)
            .Bind(w => BookmarkNamed(w, remove.Bookmark).Bind(b => manager.RemoveBookmark(w.Id, b.Id).Map(() => new { removed = b.Name, workspace = w.Name, id = b.Id })))
            .Match(Success, ControlReply.Fail);

    // The chip's click: switch to the workspace and start the bookmark (BookmarkOpener.Open).
    ControlReply BookmarkOpen(BookmarkRequest.Open open) =>
        bookmarks is null
            ? ControlReply.Fail(NoOpener)
            : WorkspaceNamed(open.Workspace)
                .Bind(w => BookmarkNamed(w, open.Bookmark).Bind(b => bookmarks.Open(w.Id, b.Id).Map(() => new { opened = b.Name, workspace = w.Name, id = b.Id })))
                .Match(Success, ControlReply.Fail);

    // "Open all bookmarks": every chip that is showing, in order. A failure lists each one that failed, by name.
    ControlReply BookmarkOpenAll(BookmarkRequest.OpenAll openAll) =>
        bookmarks is null
            ? ControlReply.Fail(NoOpener)
            : WorkspaceNamed(openAll.Workspace)
                .Bind(w => bookmarks.OpenAll(w.Id).Map(opened => new { workspace = w.Name, opened = opened.Select(b => b.Name).ToList() }))
                .Match(Success, ControlReply.Fail);

    // A bookmark named by text matches its name (trimmed, ignoring case) or its id, as a workspace does. Two names
    // that match refuse, with both ids, rather than act on whichever came first.
    Result<Bookmark> BookmarkNamed(Workspace workspace, string nameOrId)
    {
        var matches = workspace.Bookmarks.Where(b => Matches(b.Name, b.Id, nameOrId)).ToList();
        return matches switch
        {
            [var only] => only,
            [] => Result.Failure<Bookmark>($"There is no bookmark named '{nameOrId}' in {workspace.Name}. `bookmark list {workspace.Name}` lists them."),
            _ => Result.Failure<Bookmark>(
                $"Two bookmarks in {workspace.Name} are named '{nameOrId.Trim()}': {string.Join(", ", matches.Select(b => b.Id))}. Name one by its id."),
        };
    }

    // Read back after a change, so the reply is what was stored (the name trimmed) rather than what was asked.
    Bookmark BookmarkById(Guid workspaceId, Guid bookmarkId) =>
        manager.BookmarkOf(workspaceId, bookmarkId).Value;
}
