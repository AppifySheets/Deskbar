using System.IO;
using System.Text;

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
// The hook's command is the script's path alone, quoted, with forward slashes: `"C:/Users/.../claude-hook.cmd"`. It
// was `cmd.exe /c "<path>"` at first, copied from Petre's bell hook, and it never ran once. Claude Code runs hook
// commands through Git Bash on Windows, and Git Bash rewrites a lone `/c` argument into the path `C:/`, so cmd.exe
// got no /c, opened an interactive prompt, read the hook's JSON from stdin as a command, failed on it and exited 0.
// Exit 0 meant Claude Code reported nothing, and the transcript showed only a cmd banner as the hook's output.
// Measured: `echo {} | cmd.exe /c echo hi` in Git Bash prints the banner, not "hi". A quoted path needs no
// translation: Git Bash runs a .cmd by itself, and cmd.exe accepts forward slashes in a quoted path too, so the same
// line works if a hook ever runs under cmd. The test runs it through Git Bash so this cannot come back unnoticed.
//
// Never throws: a skill that could not be written costs sessions a hint, and is not worth a dialog. Traced instead.
static class ClaudeSkill
{
    const string Marker = "Written by Deskbar";
    internal const string HookScriptName = "claude-hook.cmd";

    public static void Sync(bool on) =>
        Sync(on, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.NewStateDir);

    // `home` and `deskbarFolder` (where deskbar.cmd lives) are parameters so a test can point them at scratch folders.
    internal static void Sync(bool on, string home, string deskbarFolder)
    {
        try
        {
            var claude = Path.Combine(home, ".claude");
            if (!Directory.Exists(claude)) return;

            var folder = Path.Combine(claude, "skills", "deskbar");
            var file = Path.Combine(folder, "SKILL.md");
            var hookScript = Path.Combine(deskbarFolder, HookScriptName);
            if (on)
            {
                Write(folder, file);
                WriteHookScript(hookScript, Path.Combine(deskbarFolder, ControlCommandLine.ShimName));
            }
            else Remove(folder, file);
            // An entry written by an older Deskbar (`cmd.exe /c ...`) still names the script, so Sync sees it as
            // Deskbar's own, finds its command differs, and replaces it with this one.
            ClaudeHooks.Sync(on, Path.Combine(claude, "settings.json"), HookCommand(hookScript));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ClickTrace.Write($"claude skill not {(on ? "written" : "removed")}: {e.Message}");
        }
    }

    // The hook's command line: the script's path, quoted, with forward slashes (see the header for why not cmd.exe /c).
    internal static string HookCommand(string hookScript) => $"\"{hookScript.Replace('\\', '/')}\"";

    // The script the hooks run. Its output goes nowhere: a hook's output is shown to nobody useful, and an error from
    // a Deskbar that is not running is not the session's business.
    static void WriteHookScript(string path, string shim)
    {
        var quoted = shim.Replace("%", "%%");
        var text = string.Join("\r\n",
            "@rem Written by Deskbar for Claude Code's hooks: asks the bar for the user, for the session that runs it. Do not edit.",
            $"@if defined CLAUDE_PROJECT_DIR (call \"{quoted}\" attention --from \"%CLAUDE_PROJECT_DIR%\" >nul 2>&1) else (call \"{quoted}\" attention >nul 2>&1)",
            "@exit /b 0",
            "");
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void Write(string folder, string file)
    {
        var text = Text(UpdateService.DisplayVersion);
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

    // The skill. Written for a Claude session, not for a person: what Deskbar is in two sentences, then the three
    // commands a session needs, each with the one mistake that has cost time here (a move without --no-follow took
    // the user's screen along; a bare URL landed in a browser window on another workspace).
    internal static string Text(string version) =>
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

        Deskbar's Claude Code hooks already ask for the user whenever you finish a reply or wait for a permission: if
        the user is on another workspace, your workspace's row on the bar blinks and your VS Code icon glows until
        they come to it. So you rarely need to do anything. Run `deskbar.cmd attention` from your working folder only
        when you need the user in the middle of a turn, for example before waiting on something they must do in
        another window. Do not call it for routine progress.

        ## Everything else

        `deskbar.cmd help` lists every command (creating workspaces, renaming, groups, closing windows).

        """;
}
