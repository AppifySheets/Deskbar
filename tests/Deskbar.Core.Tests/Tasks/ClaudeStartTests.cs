using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// What Deskbar says to a new Claude session and where it starts it (design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md). Pure: nothing here starts a process.
public class ClaudeStartTests
{
    static readonly DateTimeOffset t0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    static readonly Guid taskId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid otherWs = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static TaskSession Session(string id, string folder, int minutes) =>
        new(id, folder, t0.AddMinutes(minutes), Maybe<DateTimeOffset>.None);

    static TaskItem Task(Guid id, string title, Maybe<Guid> workspace, string statusLine = "", params TaskSession[] sessions) =>
        new(id, title, workspace, TaskStatus.InProgress, statusLine, Maybe<string>.None, t0, t0, t0, sessions, []);

    static TaskBoard Board(params TaskItem[] tasks) => new(tasks);

    [Fact]
    public void The_first_message_names_the_task_and_asks_to_join_it()
    {
        var task = Task(taskId, "Fix the bar", Maybe<Guid>.None, "waiting for CI");

        Assert.Equal(
            $"Deskbar task {taskId}: Fix the bar\nwaiting for CI\nJoin this task (deskbar task join {taskId}) and start on it.",
            ClaudeStart.FirstMessage(task));
    }

    [Fact]
    public void The_first_message_leaves_out_an_empty_status_line()
    {
        Assert.Equal(
            $"Deskbar task {taskId}: Fix the bar\nJoin this task (deskbar task join {taskId}) and start on it.",
            ClaudeStart.FirstMessage(Task(taskId, "Fix the bar", Maybe<Guid>.None, "")));
        Assert.Equal(
            $"Deskbar task {taskId}: Fix the bar\nJoin this task (deskbar task join {taskId}) and start on it.",
            ClaudeStart.FirstMessage(Task(taskId, "Fix the bar", Maybe<Guid>.None, "   ")));
    }

    [Fact]
    public void The_first_message_is_cut_to_1000_characters()
    {
        var message = ClaudeStart.FirstMessage(Task(taskId, new string('x', 3000), Maybe<Guid>.None, new string('y', 500)));

        Assert.Equal(1000, message.Length);
        // The ask to join is what makes the session take the task up, so it is the part that is never cut.
        Assert.EndsWith($"\nJoin this task (deskbar task join {taskId}) and start on it.", message);
        Assert.StartsWith($"Deskbar task {taskId}: xxx", message);
    }

    [Fact]
    public void The_first_message_cut_never_splits_a_surrogate_pair()
    {
        var join = $"Join this task (deskbar task join {taskId}) and start on it.";
        var room = 1000 - join.Length - 1;
        var prefix = $"Deskbar task {taskId}: ";
        // The emoji's high surrogate lands on the last kept character, so a plain cut would strand it.
        var title = new string('x', room - prefix.Length - 1) + "\U0001F600" + new string('y', 50);

        var message = ClaudeStart.FirstMessage(Task(taskId, title, Maybe<Guid>.None));

        Assert.True(message.Length <= 1000);
        Assert.EndsWith("\n" + join, message);
        Assert.DoesNotContain(message.Select((c, i) => (c, i)), p =>
            (char.IsHighSurrogate(p.c) && !(p.i + 1 < message.Length && char.IsLowSurrogate(message[p.i + 1])))
            || (char.IsLowSurrogate(p.c) && !(p.i > 0 && char.IsHighSurrogate(message[p.i - 1]))));
    }

    [Fact]
    public void A_task_with_sessions_starts_in_its_latest_sessions_folder()
    {
        var task = Task(taskId, "T", Maybe<Guid>.From(ws), "",
            Session("a", @"C:\repos\Old", 1), Session("b", @"C:\repos\New", 5), Session("c", @"C:\repos\Mid", 3));
        // Another task on the same workspace must not widen the list: a task that has sessions has its own folder.
        var sibling = Task(Guid.NewGuid(), "S", Maybe<Guid>.From(ws), "", Session("z", @"C:\repos\Sibling", 9));

        Assert.Equal([@"C:\repos\New"], ClaudeStart.Folders(task, Board(task, sibling), [@"C:\repos\Editor"]));
    }

    [Fact]
    public void A_task_with_sessions_starts_at_the_repo_not_in_an_old_worktree() =>
        Assert.Equal([@"C:\repos\Deskbar"],
            ClaudeStart.Folders(
                Task(taskId, "T", Maybe<Guid>.None, "", Session("a", @"C:\repos\Deskbar\.claude\worktrees\topic", 1)),
                Board(), []));

    [Fact]
    public void A_task_without_sessions_offers_its_workspaces_folders_latest_first()
    {
        var task = Task(taskId, "T", Maybe<Guid>.From(ws));
        var a = Task(Guid.NewGuid(), "A", Maybe<Guid>.From(ws), "", Session("1", @"C:\repos\One", 1), Session("2", @"C:\repos\Two\", 7));
        var b = Task(Guid.NewGuid(), "B", Maybe<Guid>.From(ws), "", Session("3", @"c:\repos\one", 4), Session("4", "", 8));
        var elsewhere = Task(Guid.NewGuid(), "E", Maybe<Guid>.From(otherWs), "", Session("5", @"C:\repos\Elsewhere", 9));

        // Two is latest (7), then One (its latest use is 4, the lower-case spelling is the same folder), and the
        // editor folders follow, minus the one already listed.
        var folders = ClaudeStart.Folders(task, Board(task, a, b, elsewhere), [@"C:\repos\ONE", @"C:\repos\Editor", ""]);

        Assert.Equal([@"C:\repos\Two\", @"c:\repos\one", @"C:\repos\Editor"], folders);
    }

    [Fact]
    public void A_free_task_matches_only_other_free_tasks()
    {
        var task = Task(taskId, "T", Maybe<Guid>.None);
        var free = Task(Guid.NewGuid(), "F", Maybe<Guid>.None, "", Session("1", @"C:\repos\Free", 1));
        var bound = Task(Guid.NewGuid(), "B", Maybe<Guid>.From(ws), "", Session("2", @"C:\repos\Bound", 2));

        Assert.Equal([@"C:\repos\Free"], ClaudeStart.Folders(task, Board(task, free, bound), []));
    }

    [Theory]
    [InlineData(@"C:\repos\Deskbar\.claude\worktrees\topic", @"C:\repos\Deskbar")]
    [InlineData(@"C:\repos\Deskbar\.CLAUDE\Worktrees\topic\src", @"C:\repos\Deskbar")]
    [InlineData("C:/repos/Deskbar/.claude/worktrees/topic", "C:/repos/Deskbar")]
    [InlineData(@"C:\repos\Deskbar", @"C:\repos\Deskbar")]
    public void A_worktree_folder_maps_to_its_repo(string folder, string repo) =>
        Assert.Equal(repo, ClaudeStart.RepoOf(folder));

    [Fact]
    public void The_attach_window_command_is_exact()
    {
        var launch = ClaudeStart.AttachWindow("ab12cd34", @"C:\repos\Deskbar", "Fix the bar");

        Assert.Equal("wt.exe", launch.Program);
        Assert.Equal(
            ["-w", "new", "--title", "Fix the bar", "-p", "Deskbar Claude", "-d", @"C:\repos\Deskbar", "claude", "attach", "ab12cd34"],
            launch.Arguments);
        Assert.Equal(@"C:\repos\Deskbar", launch.WorkingDirectory);
    }

    // Task 9: wt reads its own command line, so a folder ending in a separator ("C:\repos\Deskbar\") would hand it a
    // quote escaped by that backslash. The folder goes without it; a drive's root keeps its own.
    [Theory]
    [InlineData(@"C:\repos\Deskbar\", @"C:\repos\Deskbar")]
    [InlineData("C:/repos/Deskbar/", "C:/repos/Deskbar")]
    [InlineData(@"C:\", @"C:\")]
    public void The_attach_window_passes_the_folder_without_a_trailing_separator(string folder, string passed)
    {
        var launch = ClaudeStart.AttachWindow("ab12cd34", folder, "T · Claude");

        Assert.Equal(passed, launch.Arguments[launch.Arguments.ToList().IndexOf("-d") + 1]);
    }

    // The window's title (Task 9 decision): the task's title cleaned for wt, then " · Claude". wt splits its command
    // line at ';', so each becomes ','; a leading '-' would read as an option; the title is cut to 80 characters; and a
    // title with line breaks gives its first line, the one the bar shows.
    [Theory]
    [InlineData("Fix the bar", "Fix the bar · Claude")]
    [InlineData("Fix; the bar;", "Fix, the bar, · Claude")]
    [InlineData(" --  dash first", "dash first · Claude")]
    [InlineData("First line\nsecond line", "First line · Claude")]
    public void The_attach_title_is_the_clean_title_and_Claude(string title, string expected) =>
        Assert.Equal(expected, ClaudeStart.AttachTitle(title));

    [Fact]
    public void The_attach_title_cuts_a_long_title_to_80_characters() =>
        Assert.Equal(new string('x', 80) + " · Claude", ClaudeStart.AttachTitle(new string('x', 120)));

    // "Open a terminal there so you can trust it?": plain `claude` in the folder, a new window, no profile, so Petre
    // answers the trust prompt himself.
    [Fact]
    public void The_trust_window_runs_plain_claude_in_the_folder()
    {
        var launch = ClaudeStart.TrustWindow(@"C:\repos\Deskbar\");

        Assert.Equal("wt.exe", launch.Program);
        Assert.Equal(["-w", "new", "-d", @"C:\repos\Deskbar", "claude"], launch.Arguments);
        Assert.Equal(@"C:\repos\Deskbar", launch.WorkingDirectory);
    }

    // Task dbe25323: on a machine whose PATH does not lead to claude, the windows run the claude.exe Deskbar found, as
    // one argument however many spaces its folder has (ArgumentList quotes it for wt).
    const string FoundClaude = @"C:\Users\some one\.local\bin\claude.exe";

    [Fact]
    public void The_attach_window_runs_the_claude_it_is_given() =>
        Assert.Equal(
            ["-w", "new", "--title", "T", "-p", "Deskbar Claude", "-d", @"C:\repos\Deskbar", FoundClaude, "attach", "ab12cd34"],
            ClaudeStart.AttachWindow("ab12cd34", @"C:\repos\Deskbar", "T", FoundClaude).Arguments);

    [Fact]
    public void The_trust_window_runs_the_claude_it_is_given() =>
        Assert.Equal(["-w", "new", "-d", @"C:\repos\Deskbar", FoundClaude], ClaudeStart.TrustWindow(@"C:\repos\Deskbar", FoundClaude).Arguments);

    // Task abfe1db8 (Windows Server 2022 has no Windows Terminal): the window on a session is then a console of
    // claude.exe's own, on `attach <short id>` in the folder (without its trailing separator), titled as the terminal's.
    [Fact]
    public void The_attach_console_runs_claude_attach_in_the_folder_with_the_title()
    {
        var launch = ClaudeStart.AttachConsole("ab12cd34", @"C:\repos\Deskbar\", "Fix the bar · Claude", FoundClaude);

        Assert.Equal(FoundClaude, launch.Program);
        Assert.Equal(["attach", "ab12cd34"], launch.Arguments);
        Assert.Equal(@"C:\repos\Deskbar", launch.WorkingDirectory);
        Assert.Equal("Fix the bar · Claude", launch.Title.GetValueOrDefault());
    }

    // The trust console runs plain claude and leaves the title to it, as the default profile does in Windows Terminal.
    [Fact]
    public void The_trust_console_runs_plain_claude_without_a_title()
    {
        var launch = ClaudeStart.TrustConsole(@"C:\repos\Deskbar\");

        Assert.Equal("claude", launch.Program);
        Assert.Empty(launch.Arguments);
        Assert.Equal(@"C:\repos\Deskbar", launch.WorkingDirectory);
        Assert.False(launch.Title.HasValue);
    }

    // CreateProcess gets one command line: the program always quoted (its folder can hold spaces), plain arguments as
    // they are, and an argument with a space or a quote quoted by the C runtime's rules.
    [Fact]
    public void The_console_command_line_quotes_the_program() =>
        Assert.Equal($"\"{FoundClaude}\" attach ab12cd34", ClaudeStart.CommandLine(ClaudeStart.AttachConsole("ab12cd34", @"C:\x", "T", FoundClaude)));

    [Theory]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData(@"x\""y", "\"x\\\\\\\"y\"")]
    public void A_console_argument_with_spaces_or_quotes_is_quoted(string argument, string written) =>
        Assert.Equal("\"p\" " + written, ClaudeStart.CommandLine(new ConsoleLaunch("p", [argument], @"C:\", Maybe<string>.None)));

    // Windows Terminal is there when wt.exe is in a PATH folder, or as the execution alias its package puts in
    // %LOCALAPPDATA%\Microsoft\WindowsApps; Server 2022 has neither.
    [Fact]
    public void Windows_terminal_is_found_on_the_path() =>
        Assert.True(ClaudeStart.HasWindowsTerminal(@"C:\Windows; ""D:\tools\wt"" ", @"C:\Users\me\AppData\Local", f => f == @"D:\tools\wt\wt.exe"));

    [Fact]
    public void Windows_terminal_is_found_as_the_execution_alias_off_the_path() =>
        Assert.True(ClaudeStart.HasWindowsTerminal(@"C:\Windows", @"C:\Users\me\AppData\Local",
            f => f == @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe"));

    [Fact]
    public void Without_wt_exe_anywhere_there_is_no_windows_terminal() =>
        Assert.False(ClaudeStart.HasWindowsTerminal(@"C:\Windows;C:\Windows\System32", @"C:\Users\me\AppData\Local", f => !f.EndsWith("wt.exe")));

    // The folders of a workspace's VS Code windows are only names (EditorFolder.Of reads them from the titles); the spec
    // resolves them to full paths through the sessions' folders. A name no session's repo ends in cannot be started in
    // and is left out; the repo of a worktree session counts, so "Deskbar" finds C:\repos\Deskbar.
    [Fact]
    public void Editor_folders_resolve_to_full_paths_through_known_session_folders()
    {
        var a = Task(Guid.NewGuid(), "A", Maybe<Guid>.From(ws), "", Session("1", @"C:\repos\Deskbar\.claude\worktrees\topic", 1));
        var b = Task(Guid.NewGuid(), "B", Maybe<Guid>.From(otherWs), "", Session("2", @"E:\work\iam.ge", 2));

        Assert.Equal([@"C:\repos\Deskbar", @"E:\work\iam.ge"],
            ClaudeStart.EditorRepos(["deskbar", "iam.ge", "Unknown", "Deskbar"], Board(a, b)));
    }

    // Task 0bd79d0e: the bar draws Claude's icon on the window Deskbar opened on a session, which is told from every other
    // terminal window by the title AttachTitle gave it, since Windows Terminal draws all of its windows from one process.
    const string Terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.25.3131.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe";

    [Fact]
    public void A_terminal_window_with_an_attach_title_is_a_session_window() =>
        Assert.True(ClaudeStart.IsSessionWindow(Terminal, ClaudeStart.AttachTitle("give claude sessions claude icon")));

    [Theory]
    [InlineData(Terminal, "Windows PowerShell")]                                          // a terminal running something else
    [InlineData(Terminal, "Claude Code")]                                                 // no suffix
    [InlineData(Terminal, "notes · Claude · draft")]                                      // the suffix is not at the end
    [InlineData(Terminal, null)]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe", "fix the bar · Claude")]  // another app with such a title
    [InlineData(null, "fix the bar · Claude")]                                            // an elevated window: no readable path
    public void Any_other_window_is_not_a_session_window(string? processPath, string? title) =>
        Assert.False(ClaudeStart.IsSessionWindow(processPath, title));

    [Fact]
    public void The_terminal_is_matched_by_its_file_name_in_any_case() =>
        Assert.True(ClaudeStart.IsSessionWindow(@"D:\tools\terminal\windowsterminal.EXE", "x · Claude"));

    [Fact]
    public void The_first_claude_exe_on_the_path_is_the_executable()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\b\claude.exe", @"C:\Users\me\.local\bin\claude.exe" };

        Assert.Equal(@"D:\b\claude.exe",
            ClaudeStart.ExecutableIn(@"C:\Windows;  ""D:\b"" ;;C:\a", @"C:\Users\me", files.Contains).GetValueOrDefault());
    }

    [Fact]
    public void Without_one_on_the_path_the_native_install_folder_is_tried() =>
        Assert.Equal(@"C:\Users\me\.local\bin\claude.exe",
            ClaudeStart.ExecutableIn(null, @"C:\Users\me", f => f == @"C:\Users\me\.local\bin\claude.exe").GetValueOrDefault());

    // An npm install puts a claude.cmd on the PATH, which carries no icon, so only an exe counts.
    [Fact]
    public void A_claude_cmd_alone_is_no_executable() =>
        Assert.True(ClaudeStart.ExecutableIn(@"C:\npm", @"C:\Users\me", f => f == @"C:\npm\claude.cmd").HasNoValue);
}
