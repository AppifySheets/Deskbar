using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rules;

namespace Deskbar.Core.Tests;

// A desktop closed in Task View while the app runs left its workspace pointing at an id that no
// longer exists: clicking the row said "Desktop ... no longer exists" until the app restarted, and
// the next start could hand the workspace a desktop another workspace already held.
//
// The first line of defence is the Destroyed event (the second half of this file). The re-bind below is
// the backup for a close that Deskbar never heard.
//
// The re-bind is lazy. It happens when the user asks for the workspace (a click, a move of a window
// into it) and never on a sweep, so a desktop closed on purpose does not come back by itself.
public class LostDesktopTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    public LostDesktopTests() => desktops.RefuseMoveToMissingDesktop = true;

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    Guid Live(string name)
    {
        var id = Guid.NewGuid();
        desktops.Desktops.Add(new DesktopInfo(id, name));
        return id;
    }

    // Stored workspaces, in order. A null desktop gives the workspace an id nothing has.
    WorkspaceManager Started(params (string Name, Guid? Desktop)[] workspaces)
    {
        store.Stored = AppState.Empty with
        {
            Workspaces = workspaces.Select(w => new Workspace(Guid.NewGuid(), w.Name, w.Desktop ?? Guid.NewGuid())).ToList(),
        };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static Workspace Named(WorkspaceManager manager, string name) =>
        manager.State.Workspaces.Single(w => w.Name == name);

    [Fact]
    public void Switching_to_a_workspace_whose_desktop_is_gone_creates_one_and_switches_to_it()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        desktops.Desktops.RemoveAll(d => d.Id == work);

        var result = manager.Switch(Named(manager, "Work").Id);

        Assert.True(result.IsSuccess);
        var created = desktops.Desktops.Single();
        Assert.Equal("Work", created.Name);
        Assert.Equal([created.Id], desktops.Switches);
        Assert.Equal(created.Id, Named(manager, "Work").DesktopId);
        Assert.Equal(created.Id, store.Stored.Workspaces.Single().DesktopId);
    }

    [Fact]
    public void A_free_desktop_with_the_same_name_is_adopted_instead_of_creating_one()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        desktops.Desktops.RemoveAll(d => d.Id == work);
        var spare = Live("work");

        var result = manager.Switch(Named(manager, "Work").Id);

        Assert.True(result.IsSuccess);
        Assert.Equal([spare], desktops.Desktops.Select(d => d.Id));
        Assert.Equal(spare, Named(manager, "Work").DesktopId);
        Assert.Equal([spare], desktops.Switches);
    }

    [Fact]
    public void A_same_named_desktop_held_by_another_workspace_is_not_adopted()
    {
        var a = Live("Docs");
        var b = Live("Mail");
        var manager = Started(("Docs", a), ("Mail", b));
        // Mail's desktop is closed, and Docs' desktop gets renamed to Mail in Task View.
        desktops.Desktops.RemoveAll(d => d.Id == b);
        desktops.Rename(a, "Mail");

        Assert.True(manager.Switch(Named(manager, "Mail").Id).IsSuccess);

        Assert.Equal(a, Named(manager, "Docs").DesktopId);
        Assert.NotEqual(a, Named(manager, "Mail").DesktopId);
        Assert.Equal(2, desktops.Desktops.Count);
        Assert.Equal(2, manager.State.Workspaces.Select(w => w.DesktopId).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void At_start_a_workspace_keeps_its_live_desktop_whatever_the_list_order(bool lostFirst)
    {
        var held = Live("Mail");
        var workspaces = new (string, Guid?)[] { ("Docs", held), ("Mail", null) };
        if (lostFirst) workspaces = workspaces.Reverse().ToArray();

        var manager = Started(workspaces);

        Assert.Equal(held, Named(manager, "Docs").DesktopId);
        var mail = Named(manager, "Mail").DesktopId;
        Assert.NotEqual(held, mail);
        Assert.Contains(desktops.Desktops, d => d.Id == mail && d.Name == "Mail");
    }

    [Fact]
    public void At_start_two_lost_workspaces_cannot_both_adopt_the_one_free_desktop()
    {
        var spare = Live("Work");

        var manager = Started(("Work", null), ("work", null));

        var ids = manager.State.Workspaces.Select(w => w.DesktopId).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains(spare, ids);
        Assert.Equal(2, desktops.Desktops.Count);
    }

    [Fact]
    public void A_close_that_was_never_heard_changes_nothing_until_the_workspace_is_used()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        var saves = store.SaveCount;

        desktops.Desktops.RemoveAll(d => d.Id == work);
        manager.RepairWindowList();
        desktops.CurrentChangedSubject.OnNext(Guid.NewGuid());

        Assert.Empty(desktops.Desktops);
        Assert.Equal(saves, store.SaveCount);
        Assert.Equal(work, Named(manager, "Work").DesktopId);
    }

    [Fact]
    public void Switching_to_a_workspace_with_a_live_desktop_writes_nothing_and_creates_nothing()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        var saves = store.SaveCount;

        Assert.True(manager.Switch(Named(manager, "Work").Id).IsSuccess);

        Assert.Equal(saves, store.SaveCount);
        Assert.Single(desktops.Desktops);
        Assert.Equal([work], desktops.Switches);
    }

    [Fact]
    public void A_shell_that_cannot_list_desktops_makes_the_switch_fail_and_leaves_the_state_alone()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        desktops.Desktops.RemoveAll(d => d.Id == work);
        var before = manager.State;
        var saves = store.SaveCount;
        desktops.FailGetDesktops = true;

        var result = manager.Switch(Named(manager, "Work").Id);

        Assert.True(result.IsFailure);
        Assert.Same(before, manager.State);
        Assert.Equal(saves, store.SaveCount);
        Assert.Empty(desktops.Switches);
    }

    [Fact]
    public void A_shell_that_cannot_create_a_desktop_makes_the_switch_fail_and_leaves_the_state_alone()
    {
        var work = Live("Work");
        var manager = Started(("Work", work));
        desktops.Desktops.RemoveAll(d => d.Id == work);
        var before = manager.State;
        var saves = store.SaveCount;
        desktops.FailCreate = true;

        var result = manager.Switch(Named(manager, "Work").Id);

        Assert.True(result.IsFailure);
        Assert.Same(before, manager.State);
        Assert.Equal(saves, store.SaveCount);
        Assert.Empty(desktops.Switches);
    }

    [Fact]
    public void Moving_a_window_to_a_workspace_whose_desktop_is_gone_puts_it_on_the_new_desktop()
    {
        var other = Live("Other");
        var work = Live("Work");
        var editor = Window(0x1, "Editor");
        monitor.InitialWindows.Add(editor);
        desktops.WindowPlacements[editor.Handle] = other;
        var manager = Started(("Other", other), ("Work", work));
        desktops.Desktops.RemoveAll(d => d.Id == work);

        var result = manager.AssignWindow(editor.Handle, Named(manager, "Work").Id, allowFollow: false);

        Assert.True(result.IsSuccess);
        var created = desktops.Desktops.Single(d => d.Name == "Work");
        Assert.Equal(created.Id, desktops.WindowPlacements[editor.Handle]);
        Assert.Equal(created.Id, Named(manager, "Work").DesktopId);
    }

    // --- what memory or a rule decided never re-binds ------------------------------------------
    // Nobody asked for these: a window opened by itself. A desktop closed on purpose must stay closed.

    static WindowInfo Beeper(nint handle) =>
        new(new WindowHandle(handle), 42, "Beeper", @"C:\Programs\Beeper.exe", "Beeper", @"""C:\Programs\Beeper.exe"" ");

    [Fact]
    public void A_rule_on_a_workspace_whose_desktop_is_gone_creates_nothing_and_leaves_the_window_alone()
    {
        var gone = Live("Gone");
        var alive = Live("Alive");
        var manager = Started(("Gone", gone), ("Alive", alive));
        desktops.Desktops.RemoveAll(d => d.Id == gone);
        Assert.True(manager.SetRules(
        [
            new WorkspaceRule(Named(manager, "Gone").Id, RuleMatchKind.ProcessName, "beeper"),
            new WorkspaceRule(Named(manager, "Alive").Id, RuleMatchKind.ProcessName, "slack"),
        ], []).IsSuccess);
        var workspaces = store.Stored.Workspaces.ToList();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x10)));

        Assert.Single(desktops.Desktops);
        Assert.Equal(workspaces, store.Stored.Workspaces);
        Assert.Equal(gone, Named(manager, "Gone").DesktopId);
        Assert.DoesNotContain(new WindowHandle(0x10), desktops.WindowPlacements.Keys);

        // The control: the same kind of rule on a workspace with a live desktop still places its window.
        var slack = new WindowInfo(new WindowHandle(0x11), 43, "slack", @"C:\slack.exe", "Slack", @"""C:\slack.exe""");
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, slack));
        Assert.Equal(alive, desktops.WindowPlacements[slack.Handle]);
    }

    [Fact]
    public void A_title_change_matching_a_rule_on_such_a_workspace_creates_nothing()
    {
        var gone = Live("Gone");
        var manager = Started(("Gone", gone));
        desktops.Desktops.RemoveAll(d => d.Id == gone);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x10)));
        Assert.True(manager.SetRules([new WorkspaceRule(Named(manager, "Gone").Id, RuleMatchKind.ProcessName, "beeper")], []).IsSuccess);
        var workspaces = store.Stored.Workspaces.ToList();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Beeper(0x10) with { Title = "Beeper | Inbox" }));

        Assert.Empty(desktops.Desktops);
        Assert.Equal(workspaces, store.Stored.Workspaces);
        Assert.DoesNotContain(new WindowHandle(0x10), desktops.WindowPlacements.Keys);
    }

    [Fact]
    public void Placement_memory_for_such_a_workspace_creates_nothing_when_the_window_appears()
    {
        var gone = Live("Gone");
        var id = Guid.NewGuid();
        var entry = new InventoryEntry(@"C:\Programs\Beeper.exe", @"""C:\Programs\Beeper.exe"" ", "Beeper");
        store.Stored = AppState.Empty with
        {
            Workspaces = [new Workspace(id, "Gone", gone)],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [id] = [entry] },
        };
        Assert.True(new WorkspaceManager(desktops, monitor, titles, store).Start().IsSuccess);
        desktops.Desktops.RemoveAll(d => d.Id == gone);
        var workspaces = store.Stored.Workspaces.ToList();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x10)));

        Assert.Empty(desktops.Desktops);
        Assert.Equal(workspaces, store.Stored.Workspaces);
        Assert.DoesNotContain(new WindowHandle(0x10), desktops.WindowPlacements.Keys);
    }

    // --- a desktop closed while Deskbar runs takes its workspace with it -----------------------
    // The event is only a hint: the workspace goes when the list, asked again, no longer has the desktop.

    // A workspace with a rule, a roster entry and a container home, so "gone" can be seen in all of them.
    (WorkspaceManager Manager, Guid Work, Guid Keep, Guid WorkDesktop, Guid KeepDesktop) StartedWithSetup()
    {
        var workDesktop = Live("Work");
        var keepDesktop = Live("Keep");
        var work = new Workspace(Guid.NewGuid(), "Work", workDesktop);
        var keep = new Workspace(Guid.NewGuid(), "Keep", keepDesktop);
        var entry = new InventoryEntry(@"C:\Programs\Beeper.exe", null, "Beeper");
        store.Stored = AppState.Empty with
        {
            Workspaces = [work, keep],
            WorkspaceRules =
            [
                new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "beeper"),
                new WorkspaceRule(keep.Id, RuleMatchKind.ProcessName, "slack"),
            ],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [work.Id] = [entry], [keep.Id] = [entry] },
            ContainerHomes = [new ContainerHome("Rdm", "a", work.Id), new ContainerHome("Rdm", "b", keep.Id)],
        };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, work.Id, keep.Id, workDesktop, keepDesktop);
    }

    // What Windows does when the user closes the desktop: it is gone from the list, then the event fires.
    void Close(Guid desktop)
    {
        desktops.Desktops.RemoveAll(d => d.Id == desktop);
        desktops.DestroyedSubject.OnNext(desktop);
    }

    static void AssertGone(AppState state, Guid workspace)
    {
        Assert.DoesNotContain(state.Workspaces, w => w.Id == workspace);
        Assert.DoesNotContain(state.WorkspaceRules, r => r.WorkspaceId == workspace);
        Assert.DoesNotContain(workspace, state.Inventory.Keys);
        Assert.DoesNotContain(state.ContainerHomes, h => h.WorkspaceId == workspace);
    }

    [Fact]
    public void A_destroyed_desktop_takes_its_workspace_rules_roster_and_homes_with_it()
    {
        var (manager, work, keep, workDesktop, _) = StartedWithSetup();

        Close(workDesktop);

        AssertGone(manager.State, work);
        AssertGone(store.Stored, work);
        Assert.Contains(store.Stored.Workspaces, w => w.Id == keep);
        Assert.Single(store.Stored.WorkspaceRules);
        Assert.Single(store.Stored.ContainerHomes);
        Assert.Single(desktops.Desktops);
        Assert.Empty(desktops.Removed);
        Assert.DoesNotContain(manager.WindowsByWorkspace().Value.Workspaces, g => g.Workspace.Id == work);
    }

    [Fact]
    public void A_destroyed_desktop_no_workspace_holds_changes_nothing()
    {
        var (manager, _, _, _, _) = StartedWithSetup();
        var before = manager.State;
        var saves = store.SaveCount;

        desktops.DestroyedSubject.OnNext(Guid.NewGuid());

        Assert.Same(before, manager.State);
        Assert.Equal(saves, store.SaveCount);
    }

    [Fact]
    public void An_event_while_the_list_still_has_the_desktop_changes_nothing()
    {
        var (manager, _, _, workDesktop, _) = StartedWithSetup();
        var before = manager.State;

        desktops.DestroyedSubject.OnNext(workDesktop);

        Assert.Same(before, manager.State);
        Assert.Equal(2, desktops.Desktops.Count);
    }

    [Fact]
    public void An_event_while_the_list_cannot_be_read_changes_nothing()
    {
        var (manager, _, _, workDesktop, _) = StartedWithSetup();
        var before = manager.State;
        var saves = store.SaveCount;
        desktops.Desktops.RemoveAll(d => d.Id == workDesktop);
        desktops.FailGetDesktops = true;

        desktops.DestroyedSubject.OnNext(workDesktop);

        Assert.Same(before, manager.State);
        Assert.Equal(saves, store.SaveCount);
    }

    // Explorer restarting: Windows reports desktops destroyed and the list comes back empty for a moment.
    [Fact]
    public void An_event_while_the_list_is_empty_changes_nothing()
    {
        var (manager, _, _, workDesktop, keepDesktop) = StartedWithSetup();
        var before = manager.State;
        var saves = store.SaveCount;
        desktops.Desktops.Clear();

        desktops.DestroyedSubject.OnNext(workDesktop);
        desktops.DestroyedSubject.OnNext(keepDesktop);

        Assert.Same(before, manager.State);
        Assert.Equal(saves, store.SaveCount);
    }

    [Fact]
    public void Several_events_with_a_good_list_forget_only_the_workspace_whose_desktop_is_gone()
    {
        var (manager, work, keep, workDesktop, keepDesktop) = StartedWithSetup();
        desktops.Desktops.RemoveAll(d => d.Id == workDesktop);

        desktops.DestroyedSubject.OnNext(keepDesktop);
        desktops.DestroyedSubject.OnNext(workDesktop);
        desktops.DestroyedSubject.OnNext(Guid.NewGuid());

        AssertGone(manager.State, work);
        Assert.Contains(manager.State.Workspaces, w => w.Id == keep && w.DesktopId == keepDesktop);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Our_own_delete_is_one_clean_removal_whether_the_event_comes_inside_the_call_or_after(bool insideRemove)
    {
        var (manager, work, keep, workDesktop, _) = StartedWithSetup();
        desktops.AnnounceRemovals = insideRemove;

        var result = manager.RemoveWorkspace(work);
        if (!insideRemove) desktops.DestroyedSubject.OnNext(workDesktop);

        Assert.True(result.IsSuccess);
        AssertGone(manager.State, work);
        AssertGone(store.Stored, work);
        Assert.Equal([workDesktop], desktops.Removed);
        Assert.Contains(manager.State.Workspaces, w => w.Id == keep);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Our_own_delete_of_an_empty_workspace_is_one_clean_removal_too(bool insideRemove)
    {
        var (manager, work, keep, workDesktop, _) = StartedWithSetup();
        desktops.AnnounceRemovals = insideRemove;

        var result = manager.DeleteWorkspaceIfEmpty(work);
        if (!insideRemove) desktops.DestroyedSubject.OnNext(workDesktop);

        Assert.True(result.IsSuccess);
        AssertGone(manager.State, work);
        Assert.Equal([workDesktop], desktops.Removed);
        Assert.Contains(manager.State.Workspaces, w => w.Id == keep);
    }

    [Fact]
    public void Our_own_delete_of_the_only_desktop_is_still_one_clean_removal()
    {
        // The list is empty by the time the event is heard, so the handler keeps the workspace and the
        // delete's own forgetting does the work.
        var only = Live("Only");
        var manager = Started(("Only", only));
        desktops.AnnounceRemovals = true;

        var result = manager.RemoveWorkspace(Named(manager, "Only").Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(manager.State.Workspaces);
    }

    [Fact]
    public void Closing_the_desktop_you_stand_on_throws_nothing_and_the_switcher_does_not_offer_it()
    {
        var (manager, work, keep, workDesktop, keepDesktop) = StartedWithSetup();
        desktops.CurrentDesktopId = workDesktop;
        desktops.CurrentChangedSubject.OnNext(workDesktop);
        Assert.Equal(work, manager.CurrentWorkspaceId.Value);

        Close(workDesktop);
        desktops.CurrentDesktopId = keepDesktop;

        Assert.True(manager.CurrentWorkspaceId.HasNoValue);
        var recent = manager.ByRecentUse();
        Assert.DoesNotContain(recent.Ordered, w => w.Id == work);
        Assert.Equal(keep, recent.Ordered.Single().Id);
    }

    [Fact]
    public void Closing_the_desktop_of_a_group_anchor_ends_up_where_deleting_it_does()
    {
        var a = Live("A");
        var b = Live("B");
        var c = Live("C");
        var manager = Started(("A", a), ("B", b), ("C", c));
        var (idA, idB, idC) = (Named(manager, "A").Id, Named(manager, "B").Id, Named(manager, "C").Id);
        Assert.True(manager.NestWorkspace(idB, idA).IsSuccess);
        Assert.True(manager.NestWorkspace(idC, idA).IsSuccess);

        Close(a);

        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == idA);
        Assert.Equal(2, manager.State.Workspaces.Count);
        var group = Assert.Single(manager.State.Groups);
        Assert.Null(group.AnchorWorkspaceId);
        Assert.All(manager.State.Workspaces, w => Assert.Equal(group.Id, w.GroupId));
    }

    [Fact]
    public void Closing_the_desktop_of_a_nested_child_leaves_its_parent_alone_and_ends_the_group()
    {
        var a = Live("A");
        var b = Live("B");
        var manager = Started(("A", a), ("B", b));
        var (idA, idB) = (Named(manager, "A").Id, Named(manager, "B").Id);
        Assert.True(manager.NestWorkspace(idB, idA).IsSuccess);

        Close(b);

        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == idB);
        var parent = Assert.Single(manager.State.Workspaces);
        Assert.Equal(idA, parent.Id);
        Assert.Null(parent.GroupId);
        Assert.Empty(manager.State.Groups);
    }

    [Fact]
    public void A_handler_failure_does_not_stop_later_events()
    {
        var (manager, work, _, workDesktop, _) = StartedWithSetup();
        desktops.ThrowOnGetDesktops = true;
        desktops.DestroyedSubject.OnNext(workDesktop);
        Assert.Contains(manager.State.Workspaces, w => w.Id == work);
        desktops.ThrowOnGetDesktops = false;

        Close(workDesktop);

        AssertGone(manager.State, work);
    }
}
