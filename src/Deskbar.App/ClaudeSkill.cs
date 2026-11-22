using System.IO;
using System.Text;
using System.Text.Json;

namespace Deskbar.App;

// DESKBAR TELLS CLAUDE CODE ABOUT ITSELF. Petre: "can you do it so that deskbar advertises this to other claude
// users?", then "yes, add a skill for claude per user".
//
// The control pipe (deskbar.cmd) lets a session put the windows it opens in the workspace it works in, instead of
// on whatever desktop the user is looking at. Until now a session learned that only from Petre's own CLAUDE.md,
// which no other Deskbar user has. So Deskbar writes a Claude Code skill into the user's own Claude folder,
// ~/.claude/skills/deskbar/SKILL.md, and every Claude Code session on the machine finds it: a skill's description
// is what a session reads to decide when to load it, and this one says "opening a link or a window".
//
// The rules it keeps, because it writes into another program's folder:
//
//   * Only when ~/.claude already exists. No Claude Code, no folder, nothing written.
//   * Only while the setting is on (AppState.ClaudeSkill, on by default; Manage's checkbox, `claude-skill on|off`).
//   * Rewritten only when the text differs, which is every new version (the text names it), so a session always
//     reads what the running Deskbar actually accepts.
//   * Turning it off removes the file, and only a file that says Deskbar wrote it, then the folder if that left it
//     empty. Anything else in ~/.claude is never touched.
//
// AND TWO HOOKS. Petre, choosing between a skill alone and hooks: "Add the hooks". A session asks for the user with
// `deskbar.cmd attention` (FloatingBar.Asking.cs), but a session that is waiting on a permission prompt cannot run
// anything, and that is when it most needs the user. Claude Code's own hooks fire then: Notification (a permission
// prompt, or a session left waiting for input) and Stop (a reply finished). So the same setting also keeps two
// entries in ~/.claude/settings.json that run Deskbar's hook script, beside whatever hooks the user has, and takes
// out only its own when it is turned off (ClaudeHooks).
//
// The script, claude-hook.cmd next to deskbar.cmd, exists for two reasons a bare command line could not cover. It
// always exits 0, because a Stop hook that exits 2 tells Claude to KEEP GOING, and deskbar.cmd exits 2 whenever
// Deskbar is not running. And it passes the session's folder (CLAUDE_PROJECT_DIR, which Claude Code sets for hooks)
// so the right workspace blinks.
//
// AND THREE MORE HOOKS, under a setting of their own (AppState.ClaudeTasks, Manage's "Claude Code sessions keep their
// tasks in the task list", `claude-tasks on|off`; design docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "Who gets it"). SessionStart, UserPromptSubmit and SessionEnd run claude-task-hook.cmd, the same text as
// claude-hook.cmd under a name older builds do not know (Ruling 35; ClaudeHooks says why). Both run `claude-hook` with
// the hook's JSON on stdin rather than `attention` (WriteHookScript says how), so Deskbar learns the session's id and
// event and can answer with text the session reads. Turning "Teach Claude Code about Deskbar" off takes these three out
// with the rest; turning the task setting off takes out only these three, and claude-task-hook.cmd with them.
//
// The hook's command is the script's path alone, quoted, with forward slashes: `"C:/Users/.../claude-hook.cmd"`. It
// was `cmd.exe /c "<path>"` at first, copied from Petre's bell hook, and it never ran once. Claude Code runs hook
// commands through Git Bash on Windows, and Git Bash rewrites a lone `/c` argument into the path `C:/`, so cmd.exe
// got no /c, opened an interactive prompt, read the hook's JSON from stdin as a command, failed on it and exited 0.
// Exit 0 meant Claude Code reported nothing, and the transcript showed only a cmd banner as the hook's output.
// Measured: `echo {} | cmd.exe /c echo hi` in Git Bash prints the banner, not "hi". A quoted path needs no
// translation: Git Bash runs a .cmd by itself, and cmd.exe accepts forward slashes in a quoted path too, so the same
// line works if a hook ever runs under cmd. The test runs it through Git Bash so this cannot come back unnoticed.
//
// A settings file or disk problem (IO, access, JSON) is traced and costs sessions a hint, which is not worth a dialog.
// A programming error is not swallowed: it reaches the error box, where it can be found and fixed.
static class ClaudeSkill
{
    const string Marker = "Written by Deskbar";
    internal const string HookScriptName = "claude-hook.cmd";

    // The task events' script (Ruling 35): the same text as claude-hook.cmd under a name no Deskbar before it knows, so an
    // older build swapped back in neither counts its entries as its own nor rewrites it (ClaudeHooks' header says what
    // an older Deskbar then does with it: nothing visible).
    internal const string TaskHookScriptName = "claude-task-hook.cmd";

    // `on` is "Teach Claude Code about Deskbar" (AppState.ClaudeSkill); `tasks` is whether sessions keep their tasks
    // (AppState.ClaudeTasks), which adds three hook entries and only counts while `on` does (ClaudeHooks).
    public static void Sync(bool on, bool tasks) =>
        Sync(on, tasks, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.NewStateDir);

    // `home` and `deskbarFolder` (where deskbar.cmd lives) are parameters so a test can point them at scratch folders.
    internal static void Sync(bool on, bool tasks, string home, string deskbarFolder)
    {
        try
        {
            var claude = Path.Combine(home, ".claude");
            if (!Directory.Exists(claude)) return;

            var folder = Path.Combine(claude, "skills", "deskbar");
            var file = Path.Combine(folder, "SKILL.md");
            var scripts = new HookScripts(Path.Combine(deskbarFolder, HookScriptName), Path.Combine(deskbarFolder, TaskHookScriptName));
            var settings = Path.Combine(claude, "settings.json");
            if (on)
            {
                // The hooks first: the skill tells a session whether they ask for the user, so it must know.
                // An entry written by an older Deskbar (`cmd.exe /c ...`) still names the script, so Sync sees it as
                // Deskbar's own, finds its command differs, and replaces it with this one.
                var hooksInstalled = InstallHooks(scripts, Path.Combine(deskbarFolder, ControlCommandLine.ShimName), settings, tasks);
                Write(folder, file, hooksInstalled);
            }
            else
            {
                Remove(folder, file);
                // The script goes only once no entry points at it (a settings file left alone keeps its entries).
                if (ClaudeHooks.Sync(false, tasks, settings, HookCommand(scripts.Ask), HookCommand(scripts.Task)))
                    RemoveTaskScript(scripts.Task);
            }
        }
        catch (Exception e) when (IsSettingsOrDiskProblem(e))
        {
            ClickTrace.Write($"claude skill not {(on ? "written" : "removed")}: {e.Message}");
        }
    }

    // A settings file or disk problem, as opposed to a bug in Deskbar.
    static bool IsSettingsOrDiskProblem(Exception e) =>
        e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;

    // The hook script and the entries that run it; true when the entries are in settings.json. A failure here must not
    // stop the skill from being written, so it is traced and counts as "not installed".
    // The task script is there exactly while its entries are: written before they are added, taken away once they
    // are removed (and not while a file Sync left alone may still hold them), so no entry points at a missing file.
    static bool InstallHooks(HookScripts scripts, string shim, string settings, bool tasks)
    {
        try
        {
            WriteHookScript(scripts.Ask, shim);
            if (tasks) WriteHookScript(scripts.Task, shim);
            var installed = ClaudeHooks.Sync(true, tasks, settings, HookCommand(scripts.Ask), HookCommand(scripts.Task));
            if (!tasks && installed) RemoveTaskScript(scripts.Task);
            return installed;
        }
        catch (Exception e) when (IsSettingsOrDiskProblem(e))
        {
            ClickTrace.Write($"claude hooks not installed: {e.Message}");
            return false;
        }
    }

    // Where the two scripts live: claude-hook.cmd for the ask events, claude-task-hook.cmd for the task events.
    sealed record HookScripts(string Ask, string Task);

    // Takes the task script away with its entries, but only a file Deskbar wrote (it says so on its first line), the
    // rule the skill's own removal keeps: a file of that name someone else put there is theirs.
    static void RemoveTaskScript(string path)
    {
        if (!File.Exists(path) || !File.ReadAllText(path).Contains(Marker)) return;
        File.Delete(path);
        ClickTrace.Write($"claude task hook script removed: {path}");
    }

    // The hook's command line: the script's path, quoted, with forward slashes (see the header for why not cmd.exe /c).
    internal static string HookCommand(string hookScript) => $"\"{hookScript.Replace('\\', '/')}\"";

    // The script the hooks run: `claude-hook` with the hook's JSON on stdin (plan Task 7). Three things about it:
    //
    //   * STDOUT PASSES THROUGH. What Deskbar prints is what the session reads (a SessionStart or UserPromptSubmit
    //     hook's stdout is added to its context), so there is no >nul on it. Deskbar prints nothing for the events that
    //     only ask for the user, and nothing at all when it is not running (ControlArguments.HookReply). Stderr still
    //     goes to nul: an error from Deskbar is not the session's business.
    //   * STDIN PASSES THROUGH too: cmd's `call` and the shim hand theirs to Deskbar.exe, which reads it as the JSON.
    //   * Two variables ride along as arguments, because the running Deskbar cannot see the hook's environment:
    //     CLAUDE_CODE_ENTRYPOINT and CLAUDE_CODE_SESSION_ATTENDED, which tell a headless session from one a person is
    //     in (ClaudeHookEvent). An unset one arrives as "" (cmd in a batch file), which ClaudeHookEvent reads as unset.
    //
    // The no-folder branch stays: without CLAUDE_PROJECT_DIR there is no --from, and the client adds its own folder.
    static void WriteHookScript(string path, string shim)
    {
        var quoted = shim.Replace("%", "%%");
        var hook = $"call \"{quoted}\" claude-hook --entry \"%CLAUDE_CODE_ENTRYPOINT%\" --attended \"%CLAUDE_CODE_SESSION_ATTENDED%\"";
        var text = string.Join("\r\n",
            "@rem Written by Deskbar for Claude Code's hooks: tells the bar what the session that runs it is doing. Do not edit.",
            $"@if defined CLAUDE_PROJECT_DIR ({hook} --from \"%CLAUDE_PROJECT_DIR%\" 2>nul) else ({hook} 2>nul)",
            "@exit /b 0",
            "");
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void Write(string folder, string file, bool hooksInstalled)
    {
        var text = Text(UpdateService.DisplayVersion, hooksInstalled);
        if (File.Exists(file) && File.ReadAllText(file) == text) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ClickTrace.Write($"claude skill written: {file}");
    }

    static void Remove(string folder, string file)
    {
        if (!File.Exists(file) || !File.ReadAllText(file).Contains(Marker)) return;
        File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        ClickTrace.Write($"claude skill removed: {file}");
    }

    // The "Asking for the user" paragraph: the hooks are in settings.json, or Deskbar could not add them.
    const string HooksAsk = """
        Deskbar's Claude Code hooks already ask for the user whenever you finish a reply or wait for a permission: if
        the user is on another workspace, your workspace's row on the bar blinks and your VS Code icon glows until
        they come to it. So you rarely need to do anything. Run `deskbar.cmd attention` from your working folder only
        when you need the user in the middle of a turn, for example before waiting on something they must do in
        another window. Do not call it for routine progress.
        """;

    const string HooksMissing = """
        Deskbar could not add its Claude Code hooks to settings.json, so nothing asks for the user when you finish a
        reply or wait for a permission. Run `deskbar.cmd attention` from your working folder when you need the user:
        if the user is on another workspace, your workspace's row on the bar blinks and your VS Code icon glows until
        they come to it. Do not call it for routine progress.
        """;

    // The skill. Written for a Claude session, not for a person: what Deskbar is in two sentences, then the three
    // commands a session needs, each with the one mistake that has cost time here (a move without --no-follow took
    // the user's screen along; a bare URL landed in a browser window on another workspace).
    internal static string Text(string version, bool hooksInstalled = true) =>
        $$"""
        ---
        name: deskbar
        description: Use whenever you open a link, start a program, or open any window for the user on Windows (a browser, File Explorer, an app), and when asked to move windows between virtual desktops or Deskbar workspaces. Deskbar runs on this machine and can put the window in the workspace you work in, instead of on whatever desktop the user is looking at.
        ---

        # Deskbar

        {{Marker}} {{version}}. It rewrites this file when it updates, so edits here are lost. To stop it, untick
        "Teach Claude Code about Deskbar" in Deskbar's Manage window, or run `deskbar.cmd claude-skill off`.

        Deskbar names Windows' virtual desktops; a named one is a workspace. The user is often looking at a different
        workspace from the one your VS Code window is on, and a window you open appears on the desktop the user is
        looking at, not on yours, unless Deskbar puts it there.

        Every command goes through `%APPDATA%\Deskbar\deskbar.cmd`:

        - PowerShell: `& "$env:APPDATA\Deskbar\deskbar.cmd" <command>`
        - Git Bash: `"$APPDATA/Deskbar/deskbar.cmd" <command>`

        Replies are JSON. Exit codes: 0 done, 1 refused, 2 Deskbar is not running, 3 no window matched yet. If the
        file is missing or the exit code is 2, Deskbar is not running: open things the usual way.

        ## Opening a link

        Use `deskbar.cmd open <url>` instead of Start-Process, `start`, or running a browser yourself. Run it from
        your working folder. Deskbar finds the VS Code window whose folder holds it. If that window is in a
        background workspace, a new browser window opens there and the user stays where they are; otherwise the link
        opens as usual. The reply says which happened and why.

        ## Starting a program in your workspace

        `deskbar.cmd start "<workspace>" -- <program> [arguments...]` starts it and puts its window in that workspace
        as it opens, without taking the user along. Your workspace is the `workspace` of your VS Code window in
        `deskbar.cmd windows`; its title ends with `- <your folder> - Visual Studio Code`.

        ## Moving a window you already opened

        `deskbar.cmd move "<workspace>" --hwnd <hwnd> --no-follow --wait 20`, with the hwnd from `deskbar.cmd windows`.
        Always pass `--no-follow` for a window you opened: a new window has focus, and moving the focused window
        without it takes the user's screen to that workspace. Do not use the workspace marked `current` in
        `deskbar.cmd workspaces` as yours: that is the one the user is looking at.

        ## Asking for the user

        {{(hooksInstalled ? HooksAsk : HooksMissing)}}

        ## Everything else

        `deskbar.cmd help` lists every command (creating workspaces, renaming, groups, closing windows).

        """;
}
