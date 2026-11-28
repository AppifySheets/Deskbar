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

    // The permission hook's script (Allow / Deny from a task's page; PermissionScript), on PermissionRequest while the
    // setting is on (AppState.ClaudePermissions). Its own name for the task script's reason: an older Deskbar neither
    // claims its entry nor rewrites it, refuses the verb it calls, and the script then prints nothing (the terminal asks).
    internal const string PermissionHookScriptName = "claude-permission-hook.cmd";

    // `on` is "Teach Claude Code about Deskbar" (AppState.ClaudeSkill); `tasks` is whether sessions keep their tasks
    // (AppState.ClaudeTasks), which adds three hook entries and only counts while `on` does (ClaudeHooks).
    // `waiter` is whether this machine's Claude Code honours asyncRewake (ClaudeVersion.SupportsAsyncRewake): the comment
    // waiter's Stop entry is written only then, since a build without it would hold its sessions after every turn.
    // `permissions` is AppState.ClaudePermissions AND a Claude Code known to run PermissionRequest hooks as measured
    // (ClaudeVersion.SupportsPermissionHook): the permission entry, which only counts while `tasks` does.
    public static void Sync(bool on, bool tasks, bool waiter, bool permissions = false) =>
        Sync(on, tasks, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.NewStateDir, waiter, permissions);

    // `home` and `deskbarFolder` (where deskbar.cmd lives) are parameters so a test can point them at scratch folders.
    internal static void Sync(bool on, bool tasks, string home, string deskbarFolder, bool waiter = true, bool permissions = false)
    {
        try
        {
            var claude = Path.Combine(home, ".claude");
            if (!Directory.Exists(claude)) return;

            var folder = Path.Combine(claude, "skills", "deskbar");
            var file = Path.Combine(folder, "SKILL.md");
            var scripts = new HookScripts(Path.Combine(deskbarFolder, HookScriptName), Path.Combine(deskbarFolder, TaskHookScriptName),
                Path.Combine(deskbarFolder, WaitHookScriptName), Path.Combine(deskbarFolder, PermissionHookScriptName));
            var settings = Path.Combine(claude, "settings.json");
            if (on)
            {
                // The hooks first: the skill tells a session whether they ask for the user, so it must know.
                // An entry written by an older Deskbar (`cmd.exe /c ...`) still names the script, so Sync sees it as
                // Deskbar's own, finds its command differs, and replaces it with this one.
                var hooksInstalled = InstallHooks(scripts, settings, tasks, ClaudeHooks.ImagesRule(deskbarFolder), waiter, permissions);
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
                    RemoveTaskScript(scripts.Permission);
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
    static bool InstallHooks(HookScripts scripts, string settings, bool tasks, string imagesRule, bool waiter, bool permissions)
    {
        try
        {
            WriteHookScript(scripts.Ask);
            if (tasks) WriteHookScript(scripts.Task);
            if (tasks && waiter) WriteScript(scripts.Wait, WaitScript());
            if (tasks && permissions) WriteScript(scripts.Permission, PermissionScript());
            // Without the waiter (an older Claude Code) its entry is not wanted, so Sync takes out one written before.
            // The same for the permission entry while its setting is off.
            var installed = ClaudeHooks.Sync(true, tasks, settings, HookCommand(scripts.Ask), HookCommand(scripts.Task), imagesRule,
                waiter ? HookCommand(scripts.Wait) : null, permissions ? HookCommand(scripts.Permission) : null);
            if (!tasks && installed) RemoveTaskScript(scripts.Task);
            if (!(tasks && waiter) && installed) RemoveTaskScript(scripts.Wait);
            if (!(tasks && permissions) && installed) RemoveTaskScript(scripts.Permission);
            return installed;
        }
        catch (Exception e) when (IsSettingsOrDiskProblem(e))
        {
            ClickTrace.Write($"claude hooks not installed: {e.Message}");
            return false;
        }
    }

    // Where the scripts live: claude-hook.cmd for the ask events, claude-task-hook.cmd for the task events, and
    // claude-wait-hook.cmd for the comment waiter on Stop, claude-permission-hook.cmd for PermissionRequest.
    sealed record HookScripts(string Ask, string Task, string Wait, string Permission);

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

    // THE PERMISSION HOOK (Allow / Deny from a task's page; Core's PermissionDesk and PermissionAnswer say the rest).
    // Claude Code runs it on PermissionRequest, the hook's JSON on stdin, while its own terminal dialog is already drawn
    // (probe Q1, 2026-11-26), so the terminal stays a parallel way to answer the whole time. What it does:
    //
    //   1. Makes its SLOT, claude-permission\req-<n>-<n> beside the shim, with `mkdir` (atomic), retried with new numbers
    //      when another hook has that name (the waiter's I1: %RANDOM% is seeded by the second).
    //   2. Calls Deskbar ONCE, `deskbar.cmd claude-permission --slot <name>` with stdin passed through, and takes the code
    //      STRAIGHT FROM THE CALL'S OUTPUT into a variable, `for /f` over the call itself: it never touches the disk (the
    //      review's M1: a reply file, however briefly it lived, could be read by a file watcher, which could then write a
    //      valid answer). Anything but 32 hex digits counts as no code. No code (Deskbar not running, or the question
    //      stays in the terminal) ends it at once, printing nothing.
    //   3. Every second: a slot that is gone (Deskbar dropped the question: the session moved on, the setting went off,
    //      Deskbar exited or restarted) ends it, printing nothing. An answer file is read ONCE and the slot removed; then
    //      it prints a fixed line only for exactly one line "<its code> allow|deny|auto", fetches its line from Deskbar
    //      for "<its code> fetch" (a picked suggestion, task bd55bd54), the rule of PermissionAnswer.ToolLineFor, and
    //      prints nothing for anything else. Its own deadline, `seconds` ticks of a second or
    //      more, ends it printing nothing.
    //
    // NOTHING READ IS EVER PARSED BY cmd: the reply's code and the answer's line are each set from a FOR variable
    // (substituted after the line is parsed) with delayed expansion off, and checked and compared as !code! and !line!
    // under delayed expansion (expanded after the IF is parsed), so quotes, & or | in either are text, never a command or a
    // second comparison. The lines it prints are literals of this script.
    //
    // WHERE THE CODE IS, AND THE BOUNDARY. The code is never in a file before the click (the answer file Deskbar writes at
    // the click carries it, once), never on a command line (it is compared inside this cmd, and the slot's name is
    // %RANDOM%, not the code), and never in a trace (the reply is not traced, ControlArguments.ForTrace). It does live in
    // this cmd's environment, and so in the environment of the `ping` it starts for each tick. A process of the same user
    // that can read another process's memory or environment can therefore still learn it and answer; Deskbar does not
    // defend against that, as it does not defend the user's own settings.json from such a process.
    // The call itself only takes a code from Deskbar: for this verb the client checks that the pipe's server process is
    // Deskbar's own executable before it writes the request (PipeTrust.CheckServerImage, the re-review's N1), so a program
    // that serves the pipe's name while Deskbar is closed gets nothing, and this script prints nothing.
    //
    // A DIALOG'S SLOT (AskUserQuestion, task c047d0a0; a plan to approve, ExitPlanMode, task 8dbbaa0b): the registration's reply is "<code> dialog", and such a slot takes
    // only "<code> dialog", for which the script fetches the answer line from Deskbar over the pipe and lets it through
    // (PermissionAnswer explains the one exception to printing literals, and why the line is never a file). Any other
    // second word in the reply counts as no code.
    //
    // Every path but the answers ends in `exit /b 0` with nothing on stdout: Claude Code then keeps its own dialog
    // (and never sees exit 2, which is not a decision on this event anyway).
    //
    // A late answer is harmless by construction: when the user answered in the terminal first, Claude Code has its
    // decision and the hook's later line has nothing left to decide ("whichever decides first applies", the hooks
    // reference), and Deskbar drops the question on the session's next event or poll, which ends this script.
    // Pure ASCII and no absolute path, for the reason WriteHookScript gives.
    internal static string PermissionScript(int? seconds = null)
    {
        var call = $"call \"%~dp0{ControlCommandLine.ShimName}\" {Core.Control.ControlArguments.ClaudePermissionVerb} --entry \"%CLAUDE_CODE_ENTRYPOINT%\" --attended \"%CLAUDE_CODE_SESSION_ATTENDED%\" --job \"%CLAUDE_JOB_DIR%\" --slot \"%name%\"";
        return string.Join("\r\n",
        "@rem Written by Deskbar for Claude Code's PermissionRequest hook: puts the question on the session's task page and prints the answer clicked there. Do not edit.",
        "@setlocal EnableExtensions DisableDelayedExpansion",
        $"@set \"box=%~dp0{Core.Tasks.PermissionDesk.FolderName}\"",
        "@if not exist \"%box%\" mkdir \"%box%\" >nul 2>&1",
        "@set /a tries=0",
        ":pick",
        "@set /a tries+=1",
        "@if %tries% gtr 50 exit /b 0",
        "@set \"name=req-%RANDOM%-%RANDOM%\"",
        "@set \"slot=%box%\\%name%\"",
        "@mkdir \"%slot%\" >nul 2>&1 || goto pick",
        "@set \"code=\"",
        "@set \"kind=\"",
        "@if defined CLAUDE_PROJECT_DIR goto from",
        $"@for /f \"usebackq tokens=1,2\" %%a in (`{call} 2^>nul`) do @if not defined code (set \"code=%%a\" & set \"kind=%%b\")",
        "@goto called",
        ":from",
        $"@for /f \"usebackq tokens=1,2\" %%a in (`{call} --from \"%CLAUDE_PROJECT_DIR%\" 2^>nul`) do @if not defined code (set \"code=%%a\" & set \"kind=%%b\")",
        ":called",
        "@if not defined code goto done",
        // The code is 32 hex digits or nothing counts (PermissionAnswer.Code). Checked inside a setlocal of its own, so
        // delayed expansion never touches a path that holds a "!", and only the verdict leaves it.
        "@setlocal EnableDelayedExpansion",
        "@set \"ok=\"",
        "@if not \"!code:~31,1!\"==\"\" if \"!code:~32!\"==\"\" set \"ok=1\"",
        "@set \"rest=x!code!\"",
        "@for %%h in (0 1 2 3 4 5 6 7 8 9 a b c d e f) do @set \"rest=!rest:%%h=!\"",
        "@if not \"!rest!\"==\"x\" set \"ok=\"",
        // The slot's kind is nothing (a tool permission) or exactly the dialog word (AskUserQuestion, ExitPlanMode); anything else counts as no code.
        $"@if defined kind if not \"!kind!\"==\"{Core.Tasks.PermissionAnswer.DialogKind}\" set \"ok=\"",
        "@endlocal & set \"ok=%ok%\"",
        "@if not defined ok goto done",
        $"@set /a left={seconds ?? Core.Tasks.PermissionDesk.ScriptSeconds}",
        ":wait",
        "@if not exist \"%slot%\" exit /b 0",
        $"@if exist \"%slot%\\{Core.Tasks.PermissionDesk.AnswerFile}\" goto answer",
        "@set /a left-=1",
        "@if %left% leq 0 goto done",
        "@ping -n 2 127.0.0.1 >nul",
        "@goto wait",
        ":answer",
        "@set \"line=\"",
        "@set /a lines=0",
        $"@for /f \"usebackq delims=\" %%a in (\"%slot%\\{Core.Tasks.PermissionDesk.AnswerFile}\") do @(set \"line=%%a\" & set /a lines+=1 >nul)",
        "@if not \"%lines%\"==\"1\" goto done",
        // The verdict is reached under delayed expansion and only the verdict leaves it, so the slot's path (which may
        // hold a "!") is only ever used with delayed expansion off. A dialog's slot takes the dialog word and nothing
        // else; any other slot takes allow, deny, auto or fetch and never the dialog word (PermissionAnswer.LineFor and
        // ToolLineFor). Both the dialog word and fetch end in the same fetch below.
        "@setlocal EnableDelayedExpansion",
        "@set \"verdict=\"",
        "@if not defined kind if \"!line!\"==\"!code! allow\" set \"verdict=allow\"",
        "@if not defined kind if \"!line!\"==\"!code! deny\" set \"verdict=deny\"",
        "@if not defined kind if \"!line!\"==\"!code! auto\" set \"verdict=auto\"",
        $"@if not defined kind if \"!line!\"==\"!code! {Core.Tasks.PermissionAnswer.FetchWord}\" set \"verdict=fetch\"",
        $"@if defined kind if \"!line!\"==\"!code! {Core.Tasks.PermissionAnswer.DialogWord}\" set \"verdict=fetch\"",
        "@endlocal & set \"verdict=%verdict%\"",
        "@if \"%verdict%\"==\"fetch\" goto fetch",
        "@rd /s /q \"%slot%\" >nul 2>&1",
        "@if \"%verdict%\"==\"allow\" goto allow",
        "@if \"%verdict%\"==\"deny\" goto deny",
        "@if \"%verdict%\"==\"auto\" goto auto",
        "@exit /b 0",
        // A fetched answer (a dialog's, or a picked suggestion's, task bd55bd54): the slot goes, then the script asks
        // Deskbar for the line the user's click left (`claude-permission --fetch`, PermissionDesk.Fetch), its code on
        // stdin, and the reply, one line of pure-ASCII JSON (DialogAsk.AnswerLine, PermissionAnswer.SuggestionLine), goes
        // straight through to the hook's output: cmd never parses it. The code is 32 hex digits, checked above, so echoing
        // it is safe; it is spent by now (the answer was given and is fetched once). A refusal is on stderr, dropped, and
        // prints nothing: the terminal then asks.
        ":fetch",
        "@rd /s /q \"%slot%\" >nul 2>&1",
        $"@echo %code%| call \"%~dp0{ControlCommandLine.ShimName}\" {Core.Control.ControlArguments.ClaudePermissionVerb} --fetch --slot \"%name%\" 2>nul",
        "@exit /b 0",
        ":allow",
        $"@echo {Core.Tasks.PermissionAnswer.AllowLine}",
        "@exit /b 0",
        ":deny",
        $"@echo {Core.Tasks.PermissionAnswer.DenyLine}",
        "@exit /b 0",
        ":auto",
        $"@echo {Core.Tasks.PermissionAnswer.AllowAutoLine}",
        "@exit /b 0",
        ":done",
        "@rd /s /q \"%slot%\" >nul 2>&1",
        "@exit /b 0",
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
    // SessionBriefing.Commands, which says the same in fewer words). Once it is merged, released and deployed the task is
    // done, with what to try in its status line, never a second ask to test it (task b326761d). Merged, released,
    // deployed and done are ONE ordered bullet (task 59d0aac1): as two bullets, "a merged pull request means the task is
    // done" read as complete and the deploy as a footnote, and an iam.ge session marked its task done at the release. The
    // ask names what to open as a link the tasks panel can click (task 08cdb052; reasoning above SessionBriefing.Commands).
    // A change to another app is reported once it runs there, `task deployed` (task 8470d176, phase 2; reasoning above
    // SessionBriefing.Commands and in RunningChanges' header).
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
          before it merges, a deploy only they can make, a decision), run `deskbar.cmd task ask - --session <id>` with what they must do in the first words on stdin
          ("Merge [PR 512](https://github.com/owner/repo/pull/512): CI is green"): the task waits on the user, moves to
          the top of their list, and your workspace blinks. Give whatever they must open (the pull request, a published
          page or artifact, a release) as a markdown link `[text](https://...)` or a bare address: the tasks panel
          makes it clickable, and the bar shows the link's words. A file on this machine is a link too,
          `[text](file:///C:/path/page.html)`: a page, picture, PDF or text file opens, anything else is shown in
          Explorer.
          The user answers a `task ask` on the task's page, with an answer button or a comment, so never tell them to
          answer in your terminal: the words send them to a window they do not need to open.
          A choice you put through the multiple-choice dialog (AskUserQuestion) needs no `task ask` beside it:
          Deskbar puts the dialog's questions on your task, and the user can answer them there. A `task ask` that only
          points at the dialog keeps the dialog's own question off the task.
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
          user: not while a pull request of yours waits to be merged. A change that merges finishes in one order:
          merged, released, deployed, done. Once the user has merged it, run `deskbar.cmd task answered --session
          <id>` and carry on down the order. Released: if the repository has a release step (a release workflow, an
          image build), wait for it. Deployed: the running app's own evidence (its logs, health page or deploy output)
          shows the version carrying the change; report it with `deskbar.cmd task deployed - --session <id>`, the
          evidence on stdin ("2026.11.25.39-3749d21 in the portal logs"). A change nothing runs (docs, a skill, a
          library with no host) reports "nothing to deploy: library" instead. In Deskbar's own repository Deskbar
          checks the running build itself, so done follows the release there. When a person makes the deploy (presses
          redeploy, say), hand it over with `task ask - --deploy` ("Redeploy the portal"): that is no question, so the
          task shows as merged and waiting for the deploy, not as something to answer, and your `task deployed` report
          settles it. When you cannot see where the change runs (no access to its logs, health page or deploy output),
          do not ask whether it deployed: run `deskbar.cmd task deployed - --unknown --session <id>` with what you lack
          on stdin ("no access to the Portainer logs on contabo1"). The task then shows "Can't check the deploy" with
          your reason, and you finish with `task done`. Keep plain `task ask` for what the user decides or
          answers, and for a merge. When the
          deploy waits on a pull request you did not open (a release's pull request in a stacks repository), register
          it before you ask: `deskbar.cmd task pr <url> --deploy --session <id>`. Deskbar then tells you once it is
          merged (even if it already is) and leaves your question open for the redeploy. While the question waits and
          a merged pull request of the task has no deploy reported, Deskbar wakes your idle session now and then to
          check; once the evidence shows the new version run `task deployed` (a plain question also needs `task
          answered` first), then `task done`. Never
          mark done before deployed: done claims the change runs, and `task done` stops your idle
          session. Do not ask the user to test it; put what to try in the status line ("deployed; try dragging a row
          onto a group") and the user closes the task once they have tried it and it works. Never close or delete a task: only the user closes one. A message from the user leaves a done task done; when it gives you more work on it,
          run `deskbar.cmd task join <task id> --session <id>` to put it back in progress. A follow-up for later is
          `deskbar.cmd task later - --session <id>`, its title on stdin, a note on your workspace
          linked to your task as related, so it says where it came from.
        - Link your task. Put its link near the top of everything you make for it that the user opens outside
          Deskbar: pull requests, issues, published pages and mockups (artifacts), documents and reports.
          `deskbar.cmd task link <task id>` prints it as markdown, [Deskbar task 1a2b3c4d](https://deskbar.app/task#...);
          in a page, use an <a> to the same address. Clicked, it opens the task in Deskbar, so the user can get from
          the pull request or the mockup back to the task.
        - Your repository's own rules win. The pull request rules in Deskbar's start briefing (keep it rebased on its base, let CI
          run the tests) are a default: where the repository's CLAUDE.md says otherwise (no CI on pull requests,
          rebasing only right before the merge), follow the CLAUDE.md.
        - Subtasks. A task can have subtasks, one level deep. When you join one, the join reply's `family` names the
          parent, the siblings and their sessions with transcript files: read the end of the parent session's
          transcript before you start, and stay clear of what the siblings are doing. `deskbar.cmd task family
          --session <id>` prints it again later. To split off a piece of your own work for another session, run
          `deskbar.cmd task subtask - --session <id>` with its title on stdin: a Claude session starts on it at once,
          in your repo, briefed on your work (`--model <name>` picks its model). Add `--no-start` only when the user
          asked for the piece to wait; it then sits in the backlog until a session takes it up.
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
