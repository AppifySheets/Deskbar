using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.Tests;

// Task e671709b. Petre: "when a hidden claude task opens a browser or anything, it should stay in the correct workspace".
//
// A `claude --bg` session has no window anywhere up its process chain, so the launched-by tier (#94) found no launcher
// and the window stayed on whatever desktop was in front. The chains below are the measured one
// (LaunchedBy.BackgroundSession): the window's process, the plumbing the session ran it through, the session process,
// its pty host (whose pipe name carries the short id), the daemon, WmiPrvSE, svchost.
public class HiddenSessionPlacementTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeProcessTree processes = new();

    // Short session id -> the task's workspace, standing in for TaskBoard.WorkspaceOfSession.
    readonly Dictionary<string, Guid> taskWorkspaces = [];

    const string PtyHost =
        @"C:\Users\u\.local\bin\claude.exe --bg-pty-host \\.\pipe\cc-daemon-37b43e613ddb8ec4-pty-84bee58f 200 50 -- C:\Users\u\.local\bin\claude.exe --session-id 84bee58f-32b0-4349-acfb-59997bb6ba67";

    Guid personal;
    Guid deskbar;

    static WindowInfo Window(nint handle, int pid, string process) =>
        new(new WindowHandle(handle), pid, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    // Personal is the desktop in front; Deskbar is where the session's task lives.
    WorkspaceManager Started(bool withSessions = true)
    {
        var here = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        var there = new Workspace(Guid.NewGuid(), "Deskbar", Guid.NewGuid());
        new[] { here, there }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [here, there] };

        personal = here.Id;
        deskbar = there.Id;
        desktops.CurrentDesktopId = here.DesktopId!.Value;

        var manager = new WorkspaceManager(desktops, monitor, titles, store, processes: processes,
            sessionWorkspace: withSessions
                ? id => taskWorkspaces.TryGetValue(id, out var ws) ? ws : Maybe<Guid>.None
                : null);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The measured chain, child first: chrome <- node <- cmd <- claude (session) <- claude (pty host) <- claude (daemon)
    // <- WmiPrvSE <- svchost. Only the pty host's command line matters.
    void SessionOpened(int pid, string name = "chrome")
    {
        processes.Chain((name, pid), ("node", 800), ("cmd", 801), ("claude", 802), ("claude", 803), ("claude", 804),
            ("WmiPrvSE", 805), ("svchost", 806));
        processes.Processes[pid] = processes.Processes[pid] with { Started = DateTimeOffset.Now.AddSeconds(-2) };
        processes.CommandLines[802] = @"C:\Users\u\.local\bin\claude.exe --session-id 84bee58f-32b0-4349-acfb-59997bb6ba67 ""Deskbar task""";
        processes.CommandLines[803] = PtyHost;
        processes.CommandLines[804] = @"C:\Users\u\.local\bin\claude.exe daemon run --json-path C:\Users\u\.claude\daemon.json";
    }

    WindowInfo Appears(WorkspaceManager manager, nint handle, int pid, string name = "chrome")
    {
        var window = Window(handle, pid, name);
        desktops.WindowPlacements[window.Handle] = desktops.CurrentDesktopId;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window;
    }

    // Whether the window's row on the bar wears the attention dot.
    static bool Marked(WorkspaceManager manager, WindowInfo window) =>
        manager.WindowsByWorkspace().Value.Workspaces.SelectMany(g => g.Running)
            .Single(r => r.Window.Handle == window.Handle).WantsAttention;

    Guid? WorkspaceOf(WorkspaceManager manager, WindowInfo window) =>
        manager.WindowsByWorkspace().Value.Workspaces
            .Where(g => g.Running.Any(r => r.Window.Handle == window.Handle))
            .Select(g => (Guid?)g.Workspace.Id)
            .FirstOrDefault();

    [Fact]
    public void A_window_a_hidden_session_opens_goes_to_its_tasks_workspace()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(deskbar, WorkspaceOf(manager, browser));
    }

    // It arrives out of sight, so it wears the attention dot, as a window `deskbar.cmd start` put elsewhere does.
    [Fact]
    public void The_window_is_marked_for_attention_when_it_lands_out_of_sight()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.True(Marked(manager, browser));
    }

    // The same rule as the launched-by tier: a session opening a browser says nothing about where the browser lives,
    // so what Petre taught the app is left alone.
    [Fact]
    public void The_placement_writes_no_roster_entry()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.DoesNotContain(
            manager.State.Inventory.SelectMany(entry => entry.Value),
            entry => entry.ProcessPath.Contains(browser.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    // A program the session started an hour ago (a browser Petre has been using since) is not the session's any more:
    // the started-for-its-window check of the launched-by tier applies here too.
    [Fact]
    public void A_new_window_of_a_long_running_process_stays_where_it_opened()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);
        processes.Processes[900] = processes.Processes[900] with { Started = DateTimeOffset.Now.AddHours(-1) };

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
    }

    // A session on no task, or on a free task, has nowhere to send it.
    [Fact]
    public void A_session_with_no_task_workspace_leaves_the_window_where_it_opened()
    {
        var manager = Started();
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
        Assert.False(Marked(manager, browser));
    }

    // Already in front: Windows opened it there, and there is nothing to move or mark.
    [Fact]
    public void A_task_on_the_desktop_in_front_moves_nothing()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = personal;
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
        Assert.False(Marked(manager, browser));
    }

    // A foreground session (VS Code's claude, or `claude` typed into a terminal) has no pty host: nothing changes for it.
    [Fact]
    public void A_claude_without_a_pty_host_is_not_a_hidden_session()
    {
        var manager = Started();
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);
        processes.CommandLines[803] = @"C:\Users\u\.local\bin\claude.exe --output-format stream-json";

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
    }

    // Without the board's answer (compatibility mode, every caller predating this) nothing is placed by session.
    [Fact]
    public void Without_the_task_lookup_nothing_is_placed_by_session()
    {
        var manager = Started(withSessions: false);
        taskWorkspaces["84bee58f"] = deskbar;
        SessionOpened(900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
    }

    // The pty host's pipe name, as measured on all ten background sessions running on 2026-11-25.
    [Theory]
    [InlineData(PtyHost, "84bee58f")]
    [InlineData(@"claude.exe --bg-pty-host \\.\pipe\cc-daemon-37b43e613ddb8ec4-pty-B6141ECE 200 50 -- claude.exe --resume b6141ece", "b6141ece")]
    [InlineData(@"claude.exe --session-id 84bee58f-32b0-4349-acfb-59997bb6ba67", null)]
    [InlineData(@"claude.exe attach 84bee58f", null)]
    [InlineData(@"claude.exe daemon run --json-path C:\Users\u\.claude\daemon.json", null)]
    public void The_short_id_comes_from_the_pty_hosts_pipe_name(string commandLine, string? expected) =>
        Assert.Equal(expected, LaunchedBy.PtyHostSession(commandLine).GetValueOrDefault());
}
