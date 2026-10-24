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
// Never throws: a skill that could not be written costs sessions a hint, and is not worth a dialog. Traced instead.
static class ClaudeSkill
{
    const string Marker = "Written by Deskbar";

    public static void Sync(bool on) => Sync(on, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // `home` is a parameter so a test can point it at a scratch folder.
    internal static void Sync(bool on, string home)
    {
        try
        {
            var claude = Path.Combine(home, ".claude");
            if (!Directory.Exists(claude)) return;

            var folder = Path.Combine(claude, "skills", "deskbar");
            var file = Path.Combine(folder, "SKILL.md");
            if (on) Write(folder, file);
            else Remove(folder, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ClickTrace.Write($"claude skill not {(on ? "written" : "removed")}: {e.Message}");
        }
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

        When you are blocked on the user (a question only they can answer, a decision, something they must do or
        check) or you have finished work they are waiting for, run `deskbar.cmd attention` from your working folder.
        If the user is on another workspace, your workspace's row on the bar blinks and your VS Code icon glows until
        they come to it; if they are already there, nothing happens. Do not call it for routine progress.

        ## Everything else

        `deskbar.cmd help` lists every command (creating workspaces, renaming, groups, closing windows).

        """;
}
