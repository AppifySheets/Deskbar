using System.Globalization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Control;

// Where a NEW workspace goes. Petre: "when adding a new workspace, we should say in which group and, if
// neither, at what position". At most one of these per request; none means the end of the bar, which is
// where the bar's own "add" puts one.
public abstract record Placement
{
    public sealed record AtEnd : Placement;

    // The bottom of that group (WorkspaceManager.AddWorkspaceToGroup).
    public sealed record InGroup(string Group) : Placement;

    // A 1-based top-level row of the bar, a whole group counting as one row (AddWorkspaceAtRow).
    public sealed record AtRow(int Row) : Placement;

    // Right before or after an existing workspace, at THAT workspace's depth: beside a group member it
    // joins the group. The same thing as the row menu's "Insert before…" and "Insert after…".
    public sealed record Beside(string Workspace, bool After) : Placement;

    public static Placement End { get; } = new AtEnd();
}

// The `create` command, parsed. Folder is `--folder`, the workspace's folder (Workspace.Folder, task 6edcc65a), the
// remote twin of the New workspace dialog's folder box; null when not given.
public sealed record CreateRequest(string Name, Placement Placement, string? Folder = null);

// The `move` command, parsed. Handles and titles are the two ways to name a window, and a request may
// mix them; every one of them has to find a window or nothing moves (see RemoteControl.Move).
public sealed record MoveRequest(string Workspace, bool Create, Placement Placement, IReadOnlyList<WindowHandle> Handles, IReadOnlyList<string> Titles, bool NoFollow = false)
{
    public bool NamesNoWindow => Handles.Count == 0 && Titles.Count == 0;
}

// The `start` command, parsed: which workspace the program's window belongs in, and the program itself, taken
// verbatim from after the `--` so its own arguments can never be mistaken for Deskbar's options.
public sealed record StartRequest(string Workspace, bool Create, Placement Placement, ProgramLaunch Launch);

// A program to start, as the Windows side needs it: the program (an exe, a path, or anything the shell can
// start by name, like `code`), its arguments one by one, and the folder to start it in, if any.
public sealed record ProgramLaunch(string Program, IReadOnlyList<string> Arguments, string? WorkingDirectory);

// `open <url> [--from <folder>]`: the link, and the folder the caller works in, which says which editor window,
// and so which workspace, the link belongs with (see EditorFolder). The client fills `--from` in itself.
public sealed record OpenRequest(string Url, string? From);

// `claude-hook`, parsed: CLAUDE_CODE_ENTRYPOINT, CLAUDE_CODE_SESSION_ATTENDED and CLAUDE_JOB_DIR as the hook script passed them (empty
// or a literal %NAME% when unset; ClaudeHookEvent decides what they mean), the session's folder, and the hook's JSON.
public sealed record ClaudeHookRequest(string? Entrypoint, string? Attended, string? From, string? Json, string? Job = null, string? Slot = null);

// The command line after `ctl`, with the parsing kept apart from the doing so both halves can be tested
// without a pipe or a running app.
public static class ControlArguments
{
    // --wait belongs to the CLIENT: it is how long the calling process keeps re-asking while the answer
    // is "nothing matched yet". So it is taken off here, before the rest is sent, and the running app
    // never sees it. That also keeps the app's message loop from ever being parked on a wait.
    //
    // Only BEFORE a bare `--`: what follows it is the command line of a program `start` launches, and a
    // `--wait` there is that program's own argument, not this client's.
    public static Result<(IReadOnlyList<string> Forwarded, TimeSpan Wait)> SplitWait(IReadOnlyList<string> args) =>
        args.ToList().IndexOf("--wait") is var at && (at < 0 || args.ToList().IndexOf("--") is var separator && separator >= 0 && separator < at)
            ? (args, TimeSpan.Zero)
            : at + 1 < args.Count
              && double.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
              && seconds >= 0
                ? (args.Where((_, i) => i != at && i != at + 1).ToList(), TimeSpan.FromSeconds(seconds))
                : Result.Failure<(IReadOnlyList<string>, TimeSpan)>("--wait takes a number of seconds, e.g. --wait 15");

    // Everything `create` and `move` accept after their first argument, gathered before it is judged,
    // so each refusal can name the actual conflict rather than the first option that happened to be odd.
    sealed record Options(bool Create, IReadOnlyList<Placement> Placements, IReadOnlyList<WindowHandle> Handles, IReadOnlyList<string> Titles, bool NoFollow = false, string? WorkingDirectory = null, string? Folder = null)
    {
        public static Options None { get; } = new(false, [], [], []);
    }

    // `start <workspace> [--create [placement]] [--cwd <folder>] -- <program> [arguments...]`
    //
    // The `--` is required rather than guessed at: everything after it is the program's, verbatim, so a program
    // that takes its own --title or --create cannot have them read as Deskbar's. Before it, only what makes
    // sense for a window that does not exist yet: no --hwnd or --title (there is nothing to select), and no
    // --no-follow, because a started window is never followed anyway.
    public static Result<StartRequest> ParseStart(IReadOnlyList<string> args)
    {
        var separator = args.ToList().IndexOf("--");
        const string usage = "start needs a workspace, then -- and the program, e.g. start wt-login --create -- code C:\\src\\login";
        if (separator < 0 || separator + 1 >= args.Count || string.IsNullOrWhiteSpace(args[separator + 1]))
            return Result.Failure<StartRequest>(usage);
        var program = args.Skip(separator + 1).ToList();
        return FirstArgument(args.Take(separator).ToList(), usage)
            .Bind(name => Gather(Options.None, args.Skip(1).Take(separator - 1).ToArray())
                .Ensure(o => o.Handles.Count == 0 && o.Titles.Count == 0 && !o.NoFollow,
                    "start takes no --hwnd, --title or --no-follow: the window does not exist yet, and a started window never takes you along.")
                .Ensure(o => o.Folder is null, "--folder only applies to create.")
                .Ensure(o => o.Create || o.Placements.Count == 0,
                    "--group, --position, --before and --after only apply with --create.")
                .Bind(o => OnePlacement(o).Map(placement =>
                    new StartRequest(name, o.Create, placement, new ProgramLaunch(program[0], program.Skip(1).ToList(), o.WorkingDirectory)))));
    }

    // `open <url> [--from <folder>]`. Only absolute links with a scheme (https://, file://, mailto:), because the
    // link is handed to the shell or a browser as it is, and a bare word would be run as a program.
    public static Result<OpenRequest> ParseOpen(IReadOnlyList<string> args)
    {
        const string usage = "open needs a link, e.g. open https://example.com";
        return args switch
        {
            [var url] => Link(url, usage).Map(link => new OpenRequest(link, null)),
            [var url, "--from", var folder] when Named(folder) => Link(url, usage).Map(link => new OpenRequest(link, folder)),
            ["--from", var folder, var url] when Named(folder) => Link(url, usage).Map(link => new OpenRequest(link, folder)),
            _ => Result.Failure<OpenRequest>(usage),
        };
    }

    // A bare Windows path parses as an absolute file URI, so a file link has to say file: itself.
    // This text reaches a cmd line (and, in a new window, a browser's arguments), where a quote ends the quoted run
    // and lets `&` start another command, and a line break ends the command. A real link writes a quote as %22, so
    // a raw quote or any control character is refused rather than rewritten, which keeps every valid link as typed.
    static Result<string> Link(string raw, string usage) =>
        Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
        && IsPlainText(raw.Trim())
        && (!uri.IsFile || raw.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            ? raw.Trim()
            : Result.Failure<string>($"'{raw}' is not a link. {usage}");

    // The one rule for text that reaches a command line: no quote and no control character (see Link above for why).
    // Shared with BookmarkRules, so `open` and a bookmark can never disagree about what is safe to hand to the shell.
    public static bool IsPlainText(string text) => !text.Any(c => c == '"' || char.IsControl(c));

    // Whether a link is a web page, the only kind `open` gives a browser window of its own: a mailto: or file: link
    // is the shell's to hand to whatever handles it.
    public static bool IsWeb(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // THE FIRST OF THE TWO THINGS THE CLIENT ADDS TO A COMMAND (see ControlWire for why it otherwise forwards verbatim;
    // the second, claude-hook's stdin, is below). `open`, `attention`, `task` and `claude-hook` need the caller's
    // folder, and only the asking process knows it: the running app has its own current folder, and the pipe carries the
    // arguments alone. So any of them that does not name a folder gets the client's, which is the folder of the shell
    // that ran deskbar.cmd. Every other command goes through untouched. For `claude-hook`, the hook scripts pass
    // CLAUDE_PROJECT_DIR as --from when Claude Code set it; otherwise the folder the hook was started in is the session's.
    static readonly string[] AboutTheCaller = ["open", "attention", "task", ClaudeHookVerb, ClaudeWaitVerb, ClaudePermissionVerb];

    //
    // `task` ALWAYS gets the client's folder, last, even when the caller typed a --from of its own (final review, item 4):
    // the server tells the manager session by that folder and takes the last pair as the caller (RemoteControl.SplitFrom),
    // so a typed `--from C:/x` can no longer pass the manager off as someone else. The other verbs keep the old rule, a
    // --from given is used as it is: the hooks pass CLAUDE_PROJECT_DIR that way.
    public static IReadOnlyList<string> WithCallerFolder(IReadOnlyList<string> args, string callerFolder) =>
        args.Count > 0 && AboutTheCaller.Contains(args[0], StringComparer.OrdinalIgnoreCase)
        && (args[0].Equals("task", StringComparison.OrdinalIgnoreCase) || !args.Contains("--from"))
            ? [.. args, "--from", callerFolder]
            : args;

    // A THIRD, small one (a comment wakes its session; docs/superpowers/plans/2026-11-24-comment-wakes-session.md):
    // `task comment <id> ...` run from inside a Claude Code session is marked --from-claude, so Deskbar stores it as
    // usual but does not wake the task's session with it; only Petre's own comments wake. The client knows because Claude
    // Code sets CLAUDECODE in its sessions' shells (`claudeCode`, read by the caller). A session's own form, with
    // --session, already says who wrote it.
    public const string FromClaude = "--from-claude";

    public static IReadOnlyList<string> WithCaller(IReadOnlyList<string> args, string? claudeCode) =>
        !string.IsNullOrEmpty(claudeCode) && args is ["task", "comment", _, _] && !args.Contains("--session")
            ? [.. args, FromClaude]
            : args;

    // --- stdin: the second thing the client adds ---------------------------------------------------
    //
    // Claude Code hands a hook its JSON on STDIN, and the pipe carries arguments only, so the client reads its stdin and
    // sends it on as `--json <text>` (WithHookInput). And a `task` form that takes free text takes `-` for it, which
    // means "the text is on stdin" (WithTextInput). Only those two ever read stdin: any other command started from a
    // terminal would sit waiting on the keyboard. `readInput` is only called when it is needed, so a test can prove the
    // other verbs never read. WithInput applies both; a command is never both, so stdin is read once at most.
    public static IReadOnlyList<string> WithInput(IReadOnlyList<string> args, Func<string> readInput) =>
        WithTextInput(WithHookInput(args, readInput), readInput);

    public const string ClaudeHookVerb = "claude-hook";

    // A comment wakes its session (plan 2026-11-24-comment-wakes-session, Task 3): `claude-wait` is the waiting Stop hook's
    // one call, registering its waiter. It is a hook like claude-hook in every way the client cares about (its JSON on
    // stdin, silent when Deskbar is not running), so IsClaudeHook covers both. A verb of its own rather than an option of
    // claude-hook: an older Deskbar refuses an unknown verb (the script then exits 0), whereas it would skip an unknown
    // option and treat the call as a second Stop, blinking the workspace again.
    public const string ClaudeWaitVerb = "claude-wait";

    // Allow / Deny from a task's page (RemoteControl.Permission): `claude-permission` is the permission hook's one call,
    // registering its question and its slot. A hook in every way the client cares about (its JSON on stdin, silent when
    // Deskbar is not running, the hooks' short timeouts, and ForTrace writing its JSON as a length only, so the command
    // asked about never reaches the trace), so IsClaudeHook covers it too. Its own verb, for claude-wait's reason: an
    // older Deskbar refuses it, the script then prints nothing, and the terminal dialog stays.
    public const string ClaudePermissionVerb = "claude-permission";

    // The permission hook's call, the one verb whose client must reach Deskbar itself (ControlClient, PipeTrust's N1).
    public static bool IsPermissionCall(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(ClaudePermissionVerb, StringComparison.OrdinalIgnoreCase);

    public static bool IsClaudeHook(IReadOnlyList<string> args) =>
        args.Count > 0 && (args[0].Equals(ClaudeHookVerb, StringComparison.OrdinalIgnoreCase) || args[0].Equals(ClaudeWaitVerb, StringComparison.OrdinalIgnoreCase)
            || args[0].Equals(ClaudePermissionVerb, StringComparison.OrdinalIgnoreCase));

    // An explicit --json (a person trying the verb by hand) is left as it is.
    public static IReadOnlyList<string> WithHookInput(IReadOnlyList<string> args, Func<string> readInput) =>
        IsClaudeHook(args) && !args.Contains("--json") ? [.. args, "--json", readInput()] : args;

    // FREE TEXT NEVER GOES THROUGH cmd's COMMAND LINE (review of PR B, I-1). deskbar.cmd hands its arguments on with
    // `%*`, and cmd parses that line again: Git Bash and PowerShell escape a quote inside an argument as \", which cmd
    // does not know, so the quote ends the quoted run and whatever follows is cmd's to run. Measured:
    // `task status-line 'checking "x & echo INJECTED" now'` ran `echo INJECTED`, and `task comment 'set "a > pwned.txt"'`
    // tried a redirect. A session's status line, question, comment and titles are exactly that kind of text.
    //
    // So the text-taking `task` forms accept `-` in the text's place, and the client puts its stdin there: a heredoc in
    // Git Bash (`task comment - --session <id> <<'EOF'`), a pipe in PowerShell. Stdin is bytes to cmd, never parsed.
    // A `-` that is an option's value (after --session, say) is not text. The heredoc's last line break is dropped.
    // `add` too (Petre): a script's note can carry quotes or & just as a session's text can.
    //
    // `rename` was missing until task bd4f3580: `task rename <id> -` was sent on as it stood and set the title to a
    // literal "-". Every form that takes a title or text belongs on one of these two lists; a form left off takes `-`
    // as its text without a word. The goals' two title forms (`goal add`, `goal rename`) had the same hole.
    // `deployed` (task 8470d176, phase 2): a deploy's note is a version and where it was seen, free text like the rest.
    static readonly string[] TextVerbs = ["status-line", "ask", "comment", "new", "later", "add", "describe", "comment-edit", "subtask", "rename", "deployed"];

    static readonly string[] GoalTextVerbs = ["add", "rename"];

    static bool IsTextForm(IReadOnlyList<string> args) =>
        args.Count > 2 && (IsForm(args, "task", TextVerbs) || IsForm(args, "goal", GoalTextVerbs));

    static bool IsForm(IReadOnlyList<string> args, string noun, string[] verbs) =>
        args[0].Equals(noun, StringComparison.OrdinalIgnoreCase) && verbs.Contains(args[1], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> WithTextInput(IReadOnlyList<string> args, Func<string> readInput) =>
        IsTextForm(args) && Enumerable.Range(2, args.Count - 2).Where(i => args[i] == "-" && !TakesValue(args[i - 1])).ToList() is [var at, ..]
            ? [.. args.Take(at), readInput().TrimEnd(LineBreaks), .. args.Skip(at + 1)]
            : args;

    static readonly char[] LineBreaks = ['\r', '\n'];

    static bool IsOption(string word) => word.StartsWith("--", StringComparison.Ordinal);

    // THE TEXT FORMS' FLAGS THAT TAKE NO VALUE (task a5e77647). The word after any other option is that option's value
    // (--session <id>, --workspace <name>, --parent <id>, --from <folder>, goal add's --week <date>), so a `-` there is
    // a value and not the text's place. These three stand alone: `task add --important -` puts the title's `-` straight
    // after one, and while every option was taken to have a value that `-` was skipped, stdin was never read and the
    // note was titled "-". --from-claude is the one WithCaller appends. A text form that gains a flag with no value adds
    // it here, or a `-` after it is passed on as a literal title and its text is traced in full (TextForTrace).
    // `task subtask`'s --no-start (task fa056b2f) is the fourth: `task subtask --no-start -` must still read stdin.
    static readonly string[] ValuelessFlags = ["--important", "--urgent", "--no-start", FromClaude];

    static bool TakesValue(string word) => IsOption(word) && !ValuelessFlags.Contains(word, StringComparer.Ordinal);

    // What a hook's reply becomes before it is printed. A Deskbar that is not running is not the session's business: the
    // hook prints nothing and exits 0, so a Stop hook never tells Claude to keep going (exit 2 would) and a prompt goes
    // on with no context and no error. Any other reply is kept: a refusal still goes to stderr, which the script sends
    // nowhere, and still exits non-zero, which the script turns into 0.
    public static ControlReply HookReply(IReadOnlyList<string> args, ControlReply reply) =>
        IsClaudeHook(args) && reply.ExitCode == ControlReply.NotRunning ? new ControlReply(ControlReply.Ok, "") : reply;

    // A request as the app's trace writes it (`ctl <this> -> <exit code>`, App). Every command as it came, except a hook:
    // its --json carries the user's prompt and Claude's last reply, and the trace is often on and goes out with problem
    // reports (review of Task 7, I-2). So a hook is written as its event, its session and the JSON's length only, or as
    // the length alone when the JSON does not read.
    //
    // A task's free text (a question, a comment, a status line, a title) goes the same way (review of PR B, M-4): it is
    // the session's words, so it is written as its length. A comment's task id stays, since it is no one's words.
    public static string ForTrace(IReadOnlyList<string> args) =>
        IsTextForm(args) ? string.Join(' ', TextForTrace(args))
        : !IsClaudeHook(args)
            ? string.Join(' ', args)
            : ParseClaudeHook(args.Skip(1).ToList()) is { IsSuccess: true, Value.Json: { } json }
                ? Tasks.ClaudeHookEvent.Parse(json, null, null).Match(
                    hook => $"{ClaudeHookVerb} {hook.Event} session {hook.SessionId} ({json.Length} characters of JSON)",
                    _ => $"{ClaudeHookVerb} ({json.Length} characters of JSON that does not read)")
                : $"{ClaudeHookVerb} (no JSON)";

    // The words of a task text form, its text as a length. The positional words are the text, except the id that the
    // user's `task comment <id> <markdown>` names first.
    static readonly string[] IdFirst = ["comment", "describe", "comment-edit", "rename", "deployed"];

    static IEnumerable<string> TextForTrace(IReadOnlyList<string> args)
    {
        // A word after a valueless flag (`task add --important <title>`) is positional too, so it is traced as its length.
        var positional = Enumerable.Range(2, args.Count - 2).Where(i => !IsOption(args[i]) && !TakesValue(args[i - 1])).ToList();
        // `comment`, `describe` and `comment-edit` name an id first (Ruling 58 added the last two), and so does `rename`,
        // a task's or a goal's.
        var text = IdFirst.Contains(args[1], StringComparer.OrdinalIgnoreCase) && positional.Count > 1 ? positional.Skip(1) : positional;
        return args.Select((word, i) => text.Contains(i) ? $"({word.Length} characters)" : word);
    }

    // `claude-hook [--entry <value>] [--attended <value>] [--job <value>] [--from <folder>] --json <text>`, in any order, as the server
    // receives it. Values may be EMPTY: cmd passes an unset variable as "" inside a batch file, and that is an answer
    // (ClaudeHookEvent reads it as "not set"), not a missing value. --json is required.
    public static Result<ClaudeHookRequest> ParseClaudeHook(IReadOnlyList<string> args) =>
        HookOptions(new ClaudeHookRequest(null, null, null, null), args.ToArray())
            .Ensure(r => r.Json is not null, "claude-hook needs the hook's JSON on stdin (or as --json <text>)");

    static Result<ClaudeHookRequest> HookOptions(ClaudeHookRequest so, string[] rest) => rest switch
    {
        [] => so,
        ["--entry", var value, .. var tail] => HookOptions(so with { Entrypoint = value }, tail),
        ["--attended", var value, .. var tail] => HookOptions(so with { Attended = value }, tail),
        ["--job", var value, .. var tail] => HookOptions(so with { Job = value }, tail),
        ["--from", var value, .. var tail] => HookOptions(so with { From = value }, tail),
        ["--json", var value, .. var tail] => HookOptions(so with { Json = value }, tail),
        // claude-permission only: the hook script's own slot folder (PermissionDesk), which the answer goes into.
        ["--slot", var value, .. var tail] => HookOptions(so with { Slot = value }, tail),
        // AN OPTION THIS COPY DOES NOT KNOW IS SKIPPED, with its value (final review, item 14). The hook scripts are
        // rewritten by whichever Deskbar starts last, so a newer script that passes one more variable can reach an
        // older Deskbar still running, and refusing it would silence every hook of every session until a restart.
        // The value is the next word unless that is one of the options above, so an unknown flag written without a
        // value cannot swallow --json. A stray word that is no option at all is still refused.
        [var unknown, var value, .. var tail] when IsOption(unknown) && !HookOptionNames.Contains(value) => HookOptions(so, tail),
        [var unknown, .. var tail] when IsOption(unknown) => HookOptions(so, tail),
        [var unknown, ..] => Result.Failure<ClaudeHookRequest>($"'{unknown}' is not an option of claude-hook, or it is missing its value."),
    };

    static readonly IReadOnlyList<string> HookOptionNames = ["--entry", "--attended", "--job", "--from", "--json", "--slot"];

    // `create <name> [--group <g> | --position <n> | --before <ws> | --after <ws>] [--folder <path>]`
    //
    // --folder gives the workspace its folder (task 6edcc65a), on a new workspace and on one that already exists, where
    // it replaces the folder it had; an empty value clears it. Only the path is stored: unlike the dialog, the pipe
    // makes no folder on disk, because the caller can and the running app should not guess at a typo.
    public static Result<CreateRequest> ParseCreate(IReadOnlyList<string> args) =>
        FirstArgument(args, "create needs a workspace name, e.g. create wt-login --group EC")
            .Bind(name => Gather(Options.None, args.Skip(1).ToArray())
                .Ensure(o => o.WorkingDirectory is null, "--cwd only applies to start.")
                .Ensure(o => !o.Create && !o.NoFollow && o.Handles.Count == 0 && o.Titles.Count == 0,
                    "create only takes a placement (--group, --position, --before or --after); use move to move windows.")
                .Bind(o => OnePlacement(o).Map(placement => new CreateRequest(name, placement, o.Folder))));

    // `move <workspace> [--create [placement]] [--hwnd <handle>]... [--title <text>]...`
    public static Result<MoveRequest> ParseMove(IReadOnlyList<string> args) =>
        FirstArgument(args, "move needs a workspace name first, e.g. move wt-login --create --title wt-login")
            .Bind(name => Gather(Options.None, args.Skip(1).ToArray())
                .Ensure(o => o.WorkingDirectory is null, "--cwd only applies to start.")
                .Ensure(o => o.Handles.Count > 0 || o.Titles.Count > 0, "move needs at least one --hwnd or --title to say which windows")
                .Ensure(o => o.Folder is null, "--folder only applies to create.")
                // Placement only means something for a workspace being made. Accepting it for one that
                // exists would leave the caller believing the row had moved; `reorder` does that.
                .Ensure(o => o.Create || o.Placements.Count == 0,
                    "--group, --position, --before and --after only apply with --create. To move an existing workspace use reorder or group-join.")
                .Bind(o => OnePlacement(o).Map(placement => new MoveRequest(name, o.Create, placement, o.Handles, o.Titles, o.NoFollow))));

    static Result<string> FirstArgument(IReadOnlyList<string> args, string usage) =>
        args.Count == 0 || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal)
            ? Result.Failure<string>(usage)
            : args[0].Trim();

    static Result<Placement> OnePlacement(Options options) =>
        options.Placements.Count switch
        {
            0 => Placement.End,
            1 => options.Placements[0],
            // Petre's rule is a group OR a position. Two at once would mean a position inside a group,
            // which none of these options says.
            _ => Result.Failure<Placement>("Give at most one of --group, --position, --before and --after."),
        };

    // A list pattern per option, recursing on what is left. An option missing its value falls through
    // to the last arm and is reported by name.
    static Result<Options> Gather(Options so, string[] rest) => rest switch
    {
        [] => so,
        ["--create", .. var tail] => Gather(so with { Create = true }, tail),
        // Leave the caller where they are even when the window being moved is the one they are in. See
        // WorkspaceManager.AssignWindow for the rule this switches off.
        ["--no-follow", .. var tail] => Gather(so with { NoFollow = true }, tail),
        // The folder a started program runs in (start only). Given by the caller, because the running app
        // cannot see the caller's current folder: the pipe carries only the arguments.
        ["--cwd", var folder, .. var tail] when Named(folder) => Gather(so with { WorkingDirectory = folder }, tail),
        // The workspace's folder (create only, task 6edcc65a). Blank is allowed on purpose: it clears the folder.
        ["--folder", var path, .. var tail] => Gather(so with { Folder = path }, tail),
        ["--title", var text, .. var tail] when Named(text) => Gather(so with { Titles = [.. so.Titles, text] }, tail),
        ["--hwnd", var raw, .. var tail] => Handle(raw).Bind(handle => Gather(so with { Handles = [.. so.Handles, handle] }, tail)),
        ["--group", var group, .. var tail] when Named(group) => Gather(With(so, new Placement.InGroup(group.Trim())), tail),
        ["--before", var target, .. var tail] when Named(target) => Gather(With(so, new Placement.Beside(target.Trim(), After: false)), tail),
        ["--after", var target, .. var tail] when Named(target) => Gather(With(so, new Placement.Beside(target.Trim(), After: true)), tail),
        ["--position", var raw, .. var tail] => Position(raw).Bind(row => Gather(With(so, new Placement.AtRow(row)), tail)),
        [var unknown, ..] => Result.Failure<Options>($"'{unknown}' is not an option here, or it is missing its value."),
    };

    static Options With(Options so, Placement placement) => so with { Placements = [.. so.Placements, placement] };

    static bool Named(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    // A 1-based row or position. Zero and negatives are refused rather than clamped, because they are
    // almost always an off-by-one in the caller, and clamping would hide it by doing something plausible.
    public static Result<int> Position(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) && row >= 1
            ? row
            : Result.Failure<int>($"'{raw}' is not a position. Positions count from 1, the top of the bar.");

    // Hex with 0x, which is how `windows` prints them and how Spy++ shows them, or plain decimal, which
    // is what PowerShell's MainWindowHandle gives.
    public static Result<WindowHandle> Handle(string raw) =>
        (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        && value > 0
            ? new WindowHandle((nint)value)
            : Result.Failure<WindowHandle>($"'{raw}' is not a window handle (expected 0x1A2B or a decimal number).");
}
