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

        var editors = manager.KnownWindows
            .Where(EditorFolder.IsEditor)
            .Select(w => (w.Handle, Titles: manager.OriginalTitle(w.Handle).Map(t => (IEnumerable<string>)[w.Title, t]).GetValueOrDefault([w.Title])))
            .ToList();
        var matches = request.From is { } from ? EditorFolder.Best(from, editors) : [];
        var places = matches.Select(h => manager.WorkspaceOf(h)).ToList();
        var workspaces = places.Where(p => p.HasValue).Select(p => p.Value).DistinctBy(w => w.Id).ToList();

        var window = matches.Count == 1
            ? manager.KnownWindows.Where(w => w.Handle == matches[0]).Select(w => new { hwnd = Hex(w.Handle), title = w.Title }).FirstOrDefault()
            : null;

        // Every reason the link opens as usual, in the order they are checked, so the reply names the first that
        // applied. A window that is not in any workspace counts as no answer, the same as two answers.
        var target = places.Count > 0 && places.All(p => p.HasValue) && workspaces.Count == 1 ? workspaces[0] : null;
        var browser = defaultBrowser?.Invoke() ?? Maybe<string>.None;
        var reason =
            request.From is null ? "the caller's folder is not known"
            : matches.Count == 0 ? $"no VS Code window shows a folder on {request.From}"
            : target is null ? $"{matches.Count} VS Code windows match {request.From} and they are not in one workspace"
            : manager.CurrentWorkspaceId.Map(id => id == target.Id).GetValueOrDefault(false) ? $"{target.Name} is the current workspace"
            : !ControlArguments.IsWeb(request.Url) ? "it is not a web link"
            : browser.HasNoValue ? "the default browser could not be found"
            : null;

        return reason is not null || target is null
            ? AsUsual(starter, request.Url, reason ?? "", window)
            : InNewWindow(starter, request.Url, target, browser.Value, window);
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
