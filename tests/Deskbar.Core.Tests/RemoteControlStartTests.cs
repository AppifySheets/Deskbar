using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// `deskbar.cmd start <workspace> -- <program>`. Petre: "can claude session start a window in another deskbar?",
// "can we start there, not move?", "yes, let's do it". Windows opens every new window on the desktop in front,
// so start launches the program and Deskbar claims its window as it appears (WorkspaceManager.StartedFor).
//
// The launch itself is a fake here, which records what it was asked to start and answers a process id; the
// window then "appears" through the fake monitor, as the real one would raise it.
public class RemoteControlStartTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeProcessTree processes = new();
    readonly List<ProgramLaunch> launched = [];
    DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, clock: () => now, ownProcessId: 4242, processes: processes);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    ControlReply Run(WorkspaceManager manager, int? processId, params string[] args) =>
        new RemoteControl(manager, launch: program =>
        {
            launched.Add(program);
            return Result.Success(processId);
        }).Execute(args);

    void Appear(WindowInfo window) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));

    static WindowInfo Window(nint hwnd, int pid, string process) =>
        new(new WindowHandle(hwnd), pid, process, $@"C:\apps\{process}.exe", $"{process} window", null);

    Guid? DesktopOf(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).DesktopId;

    // The plain case: the started process opens its own window, and it goes to the workspace, created on the way.
    [Fact]
    public void The_started_programs_window_goes_to_the_workspace()
    {
        var manager = Started();

        var reply = Run(manager, 5000, "start", "wt-login", "--create", "--cwd", @"C:\src\login", "--", "notepad", "notes.txt");
        Appear(Window(0x10, 5000, "notepad"));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(5000, JsonDocument.Parse(reply.Output).RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(DesktopOf(manager, "wt-login"), desktops.WindowPlacements[new WindowHandle(0x10)]);
        // Started as asked, arguments one by one, in the folder given.
        Assert.Equal("notepad", launched.Single().Program);
        Assert.Equal(["notes.txt"], launched.Single().Arguments);
        Assert.Equal(@"C:\src\login", launched.Single().WorkingDirectory);
    }

    // A launcher that starts the real app: every window of the processes below the started one is claimed, so a
    // splash screen and the main window after it both land there.
    [Fact]
    public void Windows_of_processes_below_the_started_one_go_there_too()
    {
        var manager = Started();
        manager.AddWorkspace("Review");
        processes.Chain(("app", 5002), ("launcher", 5001));

        Run(manager, 5001, "start", "Review", "--", "launcher.exe");
        Appear(Window(0x20, 5002, "app"));
        Appear(Window(0x21, 5002, "app"));

        Assert.Equal(DesktopOf(manager, "Review"), desktops.WindowPlacements[new WindowHandle(0x20)]);
        Assert.Equal(DesktopOf(manager, "Review"), desktops.WindowPlacements[new WindowHandle(0x21)]);
    }

    // The single-instance case: `chrome --new-window` hands the request to the browser already running and exits,
    // so the window belongs to a process that is not below the started one. The first new window of that exe is
    // claimed, and only the first: one opened by hand a moment later stays where it was opened.
    [Fact]
    public void A_handed_off_launch_claims_the_first_window_of_that_exe_only()
    {
        var manager = Started();
        manager.AddWorkspace("Research");

        Run(manager, processId: null, "start", "Research", "--", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "--new-window", "https://example.com");
        Appear(Window(0x30, 7000, "chrome"));
        Appear(Window(0x31, 7000, "chrome"));

        Assert.Equal(DesktopOf(manager, "Research"), desktops.WindowPlacements[new WindowHandle(0x30)]);
        Assert.DoesNotContain(new WindowHandle(0x31), desktops.WindowPlacements.Keys);
    }

    // The expectation lasts 30 seconds. A window of the same name after that is someone else's.
    [Fact]
    public void An_expectation_runs_out()
    {
        var manager = Started();
        manager.AddWorkspace("Research");

        Run(manager, processId: null, "start", "Research", "--", "chrome");
        now += TimeSpan.FromSeconds(31);
        Appear(Window(0x40, 7000, "chrome"));

        Assert.DoesNotContain(new WindowHandle(0x40), desktops.WindowPlacements.Keys);
    }

    // Asked-for outranks remembered: an app placement memory keeps in another workspace still goes where it was
    // started for.
    [Fact]
    public void Start_outranks_placement_memory()
    {
        var manager = Started();
        manager.AddWorkspace("Research");
        var home = manager.AddWorkspace("Home").Value;
        // Teach memory that notepad lives in Home, as a drag would.
        Appear(Window(0x50, 100, "notepad"));
        Assert.True(manager.AssignWindow(new WindowHandle(0x50), home.Id, allowFollow: false).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Window(0x50, 100, "notepad")));

        Run(manager, 6000, "start", "Research", "--", "notepad");
        Appear(Window(0x51, 6000, "notepad"));

        Assert.Equal(DesktopOf(manager, "Research"), desktops.WindowPlacements[new WindowHandle(0x51)]);
    }

    // --- the attention dot ---------------------------------------------------------------------
    //
    // Petre: "when claude starts a process in the collapsed deskbar, it should have a new icon", "as does teams". A
    // window started for a workspace you are not looking at wears the dot a flashing taskbar button gets.

    Overview.WindowRow RowOf(WorkspaceManager manager, string workspace, nint hwnd) =>
        manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == workspace).Running.Single(r => r.Window.Handle.Value == hwnd);

    void StandOn(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = DesktopOf(manager, workspace)!.Value;
        desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId);
    }

    void Activate(WindowInfo window) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, window));

    [Fact]
    public void A_window_started_out_of_sight_wears_the_attention_dot_until_you_look_at_it()
    {
        var manager = Started();
        manager.AddWorkspace("Research");
        manager.AddWorkspace("Here");
        StandOn(manager, "Here");
        var browser = Window(0x70, 9000, "chrome");

        Run(manager, processId: null, "start", "Research", "--", "chrome", "--new-window", "https://example.com");
        Appear(browser);
        Assert.True(RowOf(manager, "Research", 0x70).WantsAttention);

        // Windows focuses a new window as it opens, out of sight on its own desktop: that is not looking at it.
        Activate(browser);
        Assert.True(RowOf(manager, "Research", 0x70).WantsAttention);

        // Going there and looking at it is.
        StandOn(manager, "Research");
        Activate(browser);
        Assert.False(RowOf(manager, "Research", 0x70).WantsAttention);
    }

    // Started for the workspace you are on, it opens in front of you: nothing to point at.
    [Fact]
    public void A_window_started_where_you_are_gets_no_dot()
    {
        var manager = Started();
        manager.AddWorkspace("Research");
        StandOn(manager, "Research");

        Run(manager, processId: null, "start", "Research", "--", "chrome");
        Appear(Window(0x71, 9001, "chrome"));

        Assert.False(RowOf(manager, "Research", 0x71).WantsAttention);
    }

    // A launch that fails says why and withdraws its expectation, so nothing of that name is claimed afterwards.
    [Fact]
    public void A_failed_launch_reports_and_expects_nothing()
    {
        var manager = Started();
        manager.AddWorkspace("Research");

        var reply = new RemoteControl(manager, launch: _ => Result.Failure<int?>("Could not start nosuch: not found")).Execute(["start", "Research", "--", "nosuch"]);
        Appear(Window(0x60, 8000, "nosuch"));

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("not found", reply.Output);
        Assert.DoesNotContain(new WindowHandle(0x60), desktops.WindowPlacements.Keys);
    }

    // A workspace that does not exist, without --create, starts nothing.
    [Fact]
    public void An_unknown_workspace_starts_nothing()
    {
        var manager = Started();

        var reply = Run(manager, 5000, "start", "nowhere", "--", "notepad");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(launched);
    }

    // --- the command line ---------------------------------------------------------------------

    // Everything after `--` is the program's, verbatim, Deskbar-looking options included.
    [Fact]
    public void The_programs_own_arguments_are_never_read_as_options()
    {
        // As Dispatch hands it over: the arguments after the verb.
        var request = ControlArguments.ParseStart(["ws", "--", "tool", "--create", "--title", "x"]);

        Assert.True(request.IsSuccess);
        Assert.False(request.Value.Create);
        Assert.Equal(["--create", "--title", "x"], request.Value.Launch.Arguments);
    }

    [Theory]
    [InlineData("start", "ws", "notepad")]                  // no --
    [InlineData("start", "ws", "--")]                       // no program
    [InlineData("start", "ws", "--title", "x", "--", "p")]  // nothing to select yet
    [InlineData("start", "ws", "--group", "EC", "--", "p")] // placement without --create
    public void Malformed_starts_are_refused(params string[] args) =>
        Assert.True(ControlArguments.ParseStart(args.Skip(1).ToArray()).IsFailure);

    // --wait belongs to the client only before `--`; after it, it is the started program's own argument.
    [Fact]
    public void A_wait_after_the_separator_is_left_for_the_program()
    {
        var split = ControlArguments.SplitWait(["start", "ws", "--", "tool", "--wait", "5"]);

        Assert.True(split.IsSuccess);
        Assert.Equal(TimeSpan.Zero, split.Value.Wait);
        Assert.Equal(["start", "ws", "--", "tool", "--wait", "5"], split.Value.Forwarded);
    }

    [Fact]
    public void Start_is_in_the_help() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "start");
}
