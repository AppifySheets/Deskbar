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

    // The comment waiter's script (a comment wakes its session; plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md,
    // Task 4), on Stop beside the ask entry while sessions keep their tasks. Its own name for the same reason as the task
    // script's: an older Deskbar neither claims its entry nor rewrites it, and the verb it calls, claude-wait, is one an
    // older Deskbar refuses, so the script then exits 0 and nothing happens.
    internal const string WaitHookScriptName = "claude-wait-hook.cmd";

    // `on` is "Teach Claude Code about Deskbar" (AppState.ClaudeSkill); `tasks` is whether sessions keep their tasks
    // (AppState.ClaudeTasks), which adds three hook entries and only counts while `on` does (ClaudeHooks).
    // `waiter` is whether this machine's Claude Code honours asyncRewake (ClaudeVersion.SupportsAsyncRewake): the comment
    // waiter's Stop entry is written only then, since a build without it would hold its sessions after every turn.
    public static void Sync(bool on, bool tasks, bool waiter) =>
        Sync(on, tasks, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.NewStateDir, waiter);

    // `home` and `deskbarFolder` (where deskbar.cmd lives) are parameters so a test can point them at scratch folders.
    internal static void Sync(bool on, bool tasks, string home, string deskbarFolder, bool waiter = true)
    {
        try
        {
            var claude = Path.Combine(home, ".claude");
            if (!Directory.Exists(claude)) return;

            var folder = Path.Combine(claude, "skills", "deskbar");
            var file = Path.Combine(folder, "SKILL.md");
            var scripts = new HookScripts(Path.Combine(deskbarFolder, HookScriptName), Path.Combine(deskbarFolder, TaskHookScriptName),
                Path.Combine(deskbarFolder, WaitHookScriptName));
            var settings = Path.Combine(claude, "settings.json");
            if (on)
            {
                // The hooks first: the skill tells a session whether they ask for the user, so it must know.
                // An entry written by an older Deskbar (`cmd.exe /c ...`) still names the script, so Sync sees it as
                // Deskbar's own, finds its command differs, and replaces it with this one.
                var hooksInstalled = InstallHooks(scripts, settings, tasks, ClaudeHooks.ImagesRule(deskbarFolder), waiter);
                Write(folder, file, hooksInstalled, tasks);
            }
            else
            {
                Remove(folder, file);
                // The script goes only once no entry points at it (a settings file left alone keeps its entries).
                if (ClaudeHooks.Sync(false, tasks, settings, HookCommand(scripts.Ask), HookCommand(scripts.Task), ClaudeHooks.ImagesRule(deskbarFolder),
                        HookCommand(scripts.Wait)))
                {
                    RemoveTaskScript(scripts.Task);
                    RemoveTaskScript(scripts.Wait);
                }
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
    static bool InstallHooks(HookScripts scripts, string settings, bool tasks, string imagesRule, bool waiter)
    {
        try
        {
            WriteHookScript(scripts.Ask);
            if (tasks) WriteHookScript(scripts.Task);
            if (tasks && waiter) WriteScript(scripts.Wait, WaitScript());
            // Without the waiter (an older Claude Code) its entry is not wanted, so Sync takes out one written before.
            var installed = ClaudeHooks.Sync(true, tasks, settings, HookCommand(scripts.Ask), HookCommand(scripts.Task), imagesRule,
                waiter ? HookCommand(scripts.Wait) : null);
            if (!tasks && installed) RemoveTaskScript(scripts.Task);
            if (!(tasks && waiter) && installed) RemoveTaskScript(scripts.Wait);
            return installed;
        }
        catch (Exception e) when (IsSettingsOrDiskProblem(e))
        {
            ClickTrace.Write($"claude hooks not installed: {e.Message}");
            return false;
        }
    }

    // Where the scripts live: claude-hook.cmd for the ask events, claude-task-hook.cmd for the task events, and
    // claude-wait-hook.cmd for the comment waiter on Stop.
    sealed record HookScripts(string Ask, string Task, string Wait);

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
    //   * Three variables ride along as arguments, because the running Deskbar cannot see the hook's environment:
    //     CLAUDE_CODE_ENTRYPOINT and CLAUDE_CODE_SESSION_ATTENDED, which tell a headless session from one a person is
    //     in, and CLAUDE_JOB_DIR, which only a background session has and which makes it count as attended although
    //     ATTENDED says 0 (ClaudeHookEvent). An unset one arrives as "" (cmd in a batch file), which ClaudeHookEvent reads as unset.
    //
    // The no-folder branch stays: without CLAUDE_PROJECT_DIR there is no --from, and the client adds its own folder.
    //
    // THE SHIM IS CALLED BY `%~dp0`, the folder the script itself is in, where deskbar.cmd always sits beside it (review
    // of PR B, I-3). The script used to name the shim's absolute path, written UTF-8, but cmd reads a batch file in the
    // console's OEM code page: a profile path with a letter outside ASCII came out garbled, the call found no shim, and
    // with stderr sent to nul every hook died without a word. With `%~dp0` the script holds no path and is pure ASCII,
    // and cmd fills in the folder itself, in whatever letters it has.
    static void WriteHookScript(string path)
    {
        var hook = $"call \"%~dp0{ControlCommandLine.ShimName}\" claude-hook --entry \"%CLAUDE_CODE_ENTRYPOINT%\" --attended \"%CLAUDE_CODE_SESSION_ATTENDED%\" --job \"%CLAUDE_JOB_DIR%\"";
        var text = string.Join("\r\n",
            "@rem Written by Deskbar for Claude Code's hooks: tells the bar what the session that runs it is doing. Do not edit.",
            $"@if defined CLAUDE_PROJECT_DIR ({hook} --from \"%CLAUDE_PROJECT_DIR%\" 2>nul) else ({hook} 2>nul)",
            "@exit /b 0",
            "");
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // THE COMMENT WAITER (a comment wakes its session; plan 2026-11-24-comment-wakes-session, Task 4). Claude Code runs it
    // after every turn with "asyncRewake", so it keeps running while the session sits idle, and when it exits 2 its stderr
    // becomes a new message to the session (measured 2026-11-24: in under 2 seconds). What it does:
    //
    //   1. Asks Deskbar ONCE, `deskbar.cmd claude-wait` with the hook's JSON on stdin (passed through, as for the other
    //      scripts), and reads its one-line answer, "<session id> <generation>" (CommentInbox), with `for /f`, which
    //      takes the client's LF line endings as well as CRLF (`set /p` does not: the final review's C1, measured). No
    //      answer (Deskbar not running, a headless session, the task events off) ends it. It never asks Deskbar again, so
    //      a Deskbar restarted while it waits (every release restarts it) does not disturb it, and it never holds the
    //      control pipe, which serves one client at a time.
    //   2. Builds the session's two paths ITSELF, in claude-inbox beside the shim (%~dp0, the state folder, where
    //      CommentInbox keeps them): a path read back through a file would reach cmd garbled when the profile has a letter
    //      outside ASCII (I2, measured), and cmd fills in %~dp0 in whatever letters it has. The answer is ASCII only.
    //   3. The answer goes through a folder of its own, claude-inbox\wait-<n>-<n>, taken with `mkdir` (atomic: only one
    //      waiter can create it) and retried with the next random numbers if another waiter has it. %RANDOM% is seeded by
    //      the second, so waiters started together draw the same numbers and once shared one file (I1, measured).
    //   4. Every 2 seconds: if the gen file is gone (the session ended) or holds another number (a later turn started a
    //      newer waiter), it exits 0 and leaves the inbox to the newer one. If the inbox file is there, it claims it by
    //      renaming it to <inbox>.<generation> (a rename only one waiter can win), types it to stderr, deletes it and
    //      exits 2: the session wakes with the comment.
    //
    // The hook's variables are passed inside quotes on the one command line, as WriteHookScript does, so an & in one of
    // them is text, not a second command (M1). The sleep is `ping -n 3 127.0.0.1`: cmd's own `timeout` refuses a
    // redirected stdin, which a hook always has. Measured: twenty such waiters used under one second of CPU in thirty
    // seconds. Pure ASCII and no absolute path, for the reason WriteHookScript gives.
    internal static string WaitScript()
    {
        var call = $"call \"%~dp0{ControlCommandLine.ShimName}\" claude-wait --entry \"%CLAUDE_CODE_ENTRYPOINT%\" --attended \"%CLAUDE_CODE_SESSION_ATTENDED%\" --job \"%CLAUDE_JOB_DIR%\"";
        return string.Join("\r\n",
        "@rem Written by Deskbar for Claude Code's hooks: after each turn, waits for your comment on the session's task and wakes the session with it. Do not edit.",
        "@setlocal EnableExtensions DisableDelayedExpansion",
        "@set \"box=%~dp0claude-inbox\"",
        "@if not exist \"%box%\" mkdir \"%box%\" >nul 2>&1",
        "@set /a tries=0",
        ":pick",
        "@set /a tries+=1",
        "@if %tries% gtr 50 exit /b 0",
        "@set \"answer=%box%\\wait-%RANDOM%-%RANDOM%\"",
        "@mkdir \"%answer%\" >nul 2>&1 || goto pick",
        $"@if defined CLAUDE_PROJECT_DIR ({call} --from \"%CLAUDE_PROJECT_DIR%\" > \"%answer%\\reply.txt\" 2>nul) else ({call} > \"%answer%\\reply.txt\" 2>nul)",
        "@set \"sid=\"",
        "@set \"mine=\"",
        "@for /f \"usebackq tokens=1,2\" %%a in (\"%answer%\\reply.txt\") do @(set \"sid=%%a\" & set \"mine=%%b\")",
        "@rd /s /q \"%answer%\" >nul 2>&1",
        "@if not defined mine exit /b 0",
        "@set \"inbox=%box%\\%sid%.md\"",
        "@set \"gen=%box%\\%sid%.gen\"",
        ":wait",
        "@if not exist \"%gen%\" exit /b 0",
        "@set \"now=\"",
        "@set /p now=< \"%gen%\"",
        "@if not \"%now%\"==\"%mine%\" exit /b 0",
        "@if exist \"%inbox%\" (move /y \"%inbox%\" \"%inbox%.%mine%\" >nul 2>&1 && (type \"%inbox%.%mine%\" 1>&2 & del \"%inbox%.%mine%\" >nul 2>&1 & exit /b 2))",
        "@ping -n 3 127.0.0.1 >nul",
        "@goto wait",
        "");
    }

    // Writes a script Deskbar owns, only when its text changed.
    static void WriteScript(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void Write(string folder, string file, bool hooksInstalled, bool tasks)
    {
        var text = Text(UpdateService.DisplayVersion, hooksInstalled, tasks);
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

    // "Your task" (plan Task 9): the rules a session keeps for its task in the user's task list, the longer form of what
    // the SessionStart briefing names (SessionBriefing). Only while sessions keep tasks AND the hooks are installed: the
    // hooks are what hand a session its id (`--session`), and without them it could not run one of these commands.
    // A pull request ready to merge is a `task ask`, not a comment and done (task 06c2538b; the reasoning is above
    // SessionBriefing.Commands, which says the same in fewer words). Once it is merged the task is done, with what to try
    // in its status line, never a second ask to test it (task b326761d; reasoning in SessionBriefing.Commands too). The
    // ask names what to open as a link the tasks panel can click (task 08cdb052; reasoning above SessionBriefing.Commands).
    const string YourTask = """
        ## Your task

        Deskbar keeps the user's task list at the bottom of its bar, and every session works on one task in it. When
        your session starts, Deskbar's hook tells you your session id, your task if you have one, and the open tasks
        you could join. Pass `--session <your session id>` to every command below, and run them from your working
        folder.

        Free text (a title, a status line, a question, a comment) always goes on stdin: put `-` in its place and give
        the text as a heredoc in Git Bash,

            "$APPDATA/Deskbar/deskbar.cmd" task ask - --session <id> <<'EOF'
            Merge [PR 512](https://github.com/owner/repo/pull/512): CI is green
            EOF

        or pipe it in from PowerShell (`'waiting for CI' | & "$env:APPDATA\Deskbar\deskbar.cmd" task status-line - --session <id>`).
        Never put free text with quotes or & | < > on the command line: deskbar.cmd hands it to cmd, which can run part
        of it as a command.

        - Keep the task lean: say what needs the user, and nothing else. The title is a few words naming the work, the
          status line one short phrase under about 60 characters saying where it stands, and comments are rare.
        - Join or create. Once you know what the session is working on, join the task it continues with
          `deskbar.cmd task join <id> --session <id>`, or create one with `deskbar.cmd task new - --session <id>` and a
          short title on stdin. Joining one of the user's notes makes it your work. A resumed session keeps its task;
          you do not need to join it again.
        - Keep the status line current: `deskbar.cmd task status-line - --session <id>`, one short line on stdin
          ("waiting for CI on #398"), changed when what you are doing changes.
        - Ask through the task. When the next move is the user's (a pull request to merge, something to try live
          before it merges, a decision), run `deskbar.cmd task ask - --session <id>` with what they must do in the first words on stdin
          ("Merge [PR 512](https://github.com/owner/repo/pull/512): CI is green"): the task waits on the user, moves to
          the top of their list, and your workspace blinks. Give whatever they must open (the pull request, a published
          page or artifact, a release) as a markdown link `[text](https://...)` or a bare address: the tasks panel
          makes it clickable, and the bar shows the link's words. A file on this machine is a link too,
          `[text](file:///C:/path/page.html)`: a page, picture, PDF or text file opens, anything else is shown in
          Explorer.
          A pull request ready to merge is an ask, never a comment and `task done`: done sits below the waiting
          tasks, so the merge would wait where the user does not look. A reply finishing is not an ask; do not ask for
          routine progress.
        - Resolve it yourself. A message from the user does not settle your question: you do. When their reply settles
          it, or you settle it yourself, run `deskbar.cmd task answered --session <id>` straight away. The task goes
          back to in progress, the blink stops, and your comments written since you asked are settled: kept, but folded
          away for the user. `deskbar.cmd task comment-settle <comment-id> --session <id>` settles one of your own
          comments that no longer matters. Never leave a question open once it is resolved. While it is open,
          each prompt's reminder from Deskbar names it.
        - Comment rarely: `deskbar.cmd task comment - --session <id>` with markdown on stdin. A comment is for the user,
          and only for a result they must know (a decision made for them, a blocker, what a release shipped), in one or
          two short lines. No progress logs, measurements, spike notes or findings dumps: those belong in the PR, the
          repo's notes or the session itself. When the user must act or decide, say exactly what the user must do in
          the first words, through `task ask`, never a comment: a comment does not move the task to the top.
        - Mark it done with `deskbar.cmd task done --session <id>` when the work is finished and nothing waits on the
          user: not while a pull request of yours waits to be merged. Once the user has merged it, run
          `deskbar.cmd task answered --session <id>`, then `task done`. A merged pull request means the task is done:
          do not ask the user to test it. Put what to try in the status line ("merged; try dragging a row onto a
          group") and mark it done; the user closes the task once they have tried it and it works. Never close or
          delete a task: only the user closes one. A message from the user leaves a done task done; when it gives you more work on it,
          run `deskbar.cmd task join <task id> --session <id>` to put it back in progress. A follow-up for later is
          `deskbar.cmd task later - --session <id>`, its title on stdin, a note on your workspace.
        - Subtasks. A task can have subtasks, one level deep. When you join one, the join reply's `family` names the
          parent, the siblings and their sessions with transcript files: read the end of the parent session's
          transcript before you start, and stay clear of what the siblings are doing. `deskbar.cmd task family
          --session <id>` prints it again later. To split off a piece of your own work for another session, run
          `deskbar.cmd task subtask - --session <id>` with its title on stdin.
        - Related and duplicate tasks. Before you start, look through `deskbar.cmd task list` and `task list --closed`
          for the same or related work, and read what their sessions did. Link a related task with `deskbar.cmd task
          relate <task id> --session <id>` (`task unrelate` removes it); the join reply's `linked` and `task family`
          list the linked tasks with their sessions' transcripts. When your task is the same work as another, done or
          under way there, run `deskbar.cmd task duplicate-of <task id> --session <id>`: your task closes as its
          duplicate, the one close a session makes, and you do not redo the work.
        - Leave the user's own notes alone unless they ask you to change one.
        """;

    // The description's extra sentence while the task section is there (review of PR B, M-6): a session decides from
    // the description whether to load the skill, so it has to name the task too, or a session about to set its status
    // line would never read how.
    const string TaskDescription = " Also use it to keep your task in the user's Deskbar task list: joining or creating it, its status line, asking the user through it, comments, and marking it done.";

    // A section followed by the blank line that separates it from the next, in the line ending the raw literals have
    // (they take the source file's, which depends on the checkout), so the skill never mixes the two.
    static string WithBlankLine(string section)
    {
        var newLine = section.Contains('\r') ? "\r\n" : "\n";
        return section + newLine + newLine;
    }

    // The skill. Written for a Claude session, not for a person: what Deskbar is in two sentences, then the three
    // commands a session needs, each with the one mistake that has cost time here (a move without --no-follow took
    // the user's screen along; a bare URL landed in a browser window on another workspace). The "Your task" section
    // is there while sessions keep tasks and the hooks are installed (see YourTask).
    internal static string Text(string version, bool hooksInstalled = true, bool tasks = false) =>
        $$"""
        ---
        name: deskbar
        description: Use whenever you open a link, start a program, or open any window for the user on Windows (a browser, File Explorer, an app), and when asked to move windows between virtual desktops or Deskbar workspaces. Deskbar runs on this machine and can put the window in the workspace you work in, instead of on whatever desktop the user is looking at.{{(tasks && hooksInstalled ? TaskDescription : "")}}
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

        {{(tasks && hooksInstalled ? WithBlankLine(YourTask) : "")}}## Asking for the user

        {{(hooksInstalled ? HooksAsk : HooksMissing)}}

        ## Everything else

        `deskbar.cmd help` lists every command (creating workspaces, renaming, groups, closing windows).

        """;
}
