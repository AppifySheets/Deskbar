using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Control;

// OPEN A LINK WHERE THE CALLER IS WORKING. Petre: "let's have all vscode based claude sessions open new browser
// windows if they're not working in the current deskbar", "if they're in a background bar, let them start new
// processes".
//
// The problem it solves, as Petre saw it: "now browser link opened from this bar opens in my current deskbar". A
// session in a background workspace that opens a link starts msedge.exe, which hands the link to the Edge already
// running and exits within milliseconds. The tab or window then belongs to the Edge that started at login, whose
// parent is Explorer, so nothing in the process tree points back at the session, launched-by placement has nothing
// to go on, and Windows opens it on the desktop in front.
//
// So the caller says where it works (`--from`, filled in by the client), and this decides:
//
//   the caller's editor window is in a BACKGROUND workspace   a new window of the default browser, started there
//                                                             through `start`'s expectation, without switching
//   anything else (current workspace, no match, two matches   the link opens as usual: the shell hands it to the
//   in different workspaces, not a web link, no browser)     default handler, a tab on the desktop in front
//
// The usual open is the right answer for the current workspace: Chromium browsers open a link in a window on the
// desktop in front, which is where the caller is. The reply always says which happened and why, so a caller that
// expected the other can see the reason instead of guessing.
public sealed partial class RemoteControl
{
    ControlReply Open(OpenRequest request)
    {
        if (launch is not { } starter) return ControlReply.Fail("This copy of Deskbar cannot open links.");

        var caller = CallerOf(request.From);
        var target = caller.Workspace;
        var browser = defaultBrowser?.Invoke() ?? Maybe<string>.None;
        // Every reason the link opens as usual, in the order they are checked, so the reply names the first that
        // applied.
        var reason =
            caller.Missing is { } missing ? missing
            : manager.CurrentWorkspaceId.Map(id => id == target!.Id).GetValueOrDefault(false) ? $"{target!.Name} is the current workspace"
            : !ControlArguments.IsWeb(request.Url) ? "it is not a web link"
            : browser.HasNoValue ? "the default browser could not be found"
            : null;

        return reason is not null || target is null
            ? AsUsual(starter, request.Url, reason ?? "", caller.Window)
            : InNewWindow(starter, request.Url, target, browser.Value, caller.Window);
    }

    // The caller's editor window and its workspace, from the folder it works in (see EditorFolder), shared by `open`
    // and `attention`. `Missing` says why there is no workspace when there is none: no folder given, no window on
    // it, or several windows in different workspaces. A window that is in no workspace counts as no answer, the same
    // as two answers. `Handle` and `Window` are set only when exactly one window matched.
    sealed record Caller(Workspace? Workspace, Maybe<WindowHandle> Handle, object? Window, string? Missing);

    Caller CallerOf(string? from)
    {
        var editors = manager.KnownWindows
            .Where(EditorFolder.IsEditor)
            .Select(w => (w.Handle, Titles: manager.OriginalTitle(w.Handle).Map(t => (IEnumerable<string>)[w.Title, t]).GetValueOrDefault([w.Title])))
            .ToList();
        var matches = from is null ? [] : EditorFolder.Best(from, editors);
        var places = matches.Select(h => manager.WorkspaceOf(h)).ToList();
        var workspaces = places.Where(p => p.HasValue).Select(p => p.Value).DistinctBy(w => w.Id).ToList();
        var target = places.Count > 0 && places.All(p => p.HasValue) && workspaces.Count == 1 ? workspaces[0] : null;

        var handle = matches.Count == 1 ? matches[0] : Maybe<WindowHandle>.None;
        var window = handle.Bind(h => manager.KnownWindows.TryFirst(w => w.Handle == h))
            .Map(w => (object)new { hwnd = Hex(w.Handle), title = w.Title })
            .GetValueOrDefault();

        var missing =
            from is null ? "the caller's folder is not known"
            : matches.Count == 0 ? $"no VS Code window shows a folder on {from}"
            : target is null ? $"{matches.Count} VS Code windows match {from} and they are not in one workspace"
            : null;
        return new Caller(target, handle, window, missing);
    }

    // `attention [--from <folder>]`: the session in the caller's editor window wants the user. Its workspace's row
    // blinks and the window glows (WorkspaceManager.RequestAttention), unless the user is already on that workspace.
    // Answered with what happened either way, never refused for an unknown caller: a Claude Code hook runs this on
    // every notification, and a hook that fails prints an error into the session for nothing.
    ControlReply Attention(string? from)
    {
        var caller = CallerOf(from);
        if (caller.Workspace is not { } target)
            return Success(new { marked = false, reason = caller.Missing });

        return manager.RequestAttention(target.Id, caller.Handle).Match(
            marked => Success(new
            {
                marked,
                workspace = target.Name,
                reason = marked ? null : $"{target.Name} is the current workspace",
                window = caller.Window,
            }),
            ControlReply.Fail);
    }

    ControlReply AsUsual(Func<ProgramLaunch, Result<int?>> starter, string url, string reason, object? window) =>
        starter(new ProgramLaunch(url, [], null)).Match(
            _ => Success(new { url, opened = "as usual", reason, window }),
            ControlReply.Fail);

    // Through the expectation `start` uses (RemoteControl.Start): by name first, since the browser started here
    // hands the link to the copy already running and the window belongs to that one.
    ControlReply InNewWindow(Func<ProgramLaunch, Result<int?>> starter, string url, Workspace target, string browser, object? window)
    {
        var expectation = manager.ExpectWindow(browser, target.Id);
        return starter(new ProgramLaunch(browser, ["--new-window", url], null)).Match(
            processId =>
            {
                if (processId is { } started) manager.NoteExpectedProcess(expectation, started);
                return Success(new { url, opened = "new window", workspace = target.Name, browser, window });
            },
            error =>
            {
                manager.CancelExpectation(expectation);
                return ControlReply.Fail(error);
            });
    }

    // The setting that writes the Claude Code skill (AppState.ClaudeSkill), the remote twin of Manage's checkbox.
    ControlReply ClaudeSkill(string onOff) =>
        onOff.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => Reply(manager.SetClaudeSkill(true), () => new { claudeSkill = "on" }),
            "off" or "false" or "no" => Reply(manager.SetClaudeSkill(false), () => new { claudeSkill = "off" }),
            _ => ControlReply.Fail(ControlUsage.LineFor("claude-skill")),
        };
}
