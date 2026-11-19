using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// Tasks in the bar, spec "Marks on the overview": a due task blinks its workspace's row exactly like an ask, and a
// running task's timer rides on the workspace group for the bar to draw. Both are live state stamped after the build.
public class WorkspaceManagerTaskMarksTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        manager.AddWorkspace("iam.ge");
        return manager;
    }

    static Guid IdOf(WorkspaceManager manager, string workspace) => manager.State.Workspaces.Single(w => w.Name == workspace).Id;

    void StandOn(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId);
    }

    static Overview.WorkspaceGroup Group(WorkspaceManager manager, string workspace) =>
        manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == workspace);

    static readonly IReadOnlyDictionary<Guid, TaskTimer> NoTimers = new Dictionary<Guid, TaskTimer>();

    [Fact]
    public void A_due_workspace_wants_attention()
    {
        var manager = Started();
        manager.SetTaskMarks(new HashSet<Guid> { IdOf(manager, "Deskbar") }, NoTimers);

        Assert.True(Group(manager, "Deskbar").WantsAttention);
        Assert.False(Group(manager, "iam.ge").WantsAttention);
    }

    [Fact]
    public void A_running_workspace_carries_its_timer()
    {
        var manager = Started();
        var timer = new TaskTimer(Now, TimeSpan.FromMinutes(25));
        manager.SetTaskMarks(new HashSet<Guid>(), new Dictionary<Guid, TaskTimer> { [IdOf(manager, "Deskbar")] = timer });

        Assert.Equal(timer, Group(manager, "Deskbar").Timer.Value);
        Assert.True(Group(manager, "iam.ge").Timer.HasNoValue);
    }

    [Fact]
    public void Unchanged_marks_do_not_pulse()
    {
        var manager = Started();
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(__ => pulses++);
        var id = IdOf(manager, "Deskbar");
        var timer = new TaskTimer(Now, TimeSpan.FromMinutes(25));

        manager.SetTaskMarks(new HashSet<Guid> { id }, new Dictionary<Guid, TaskTimer> { [id] = timer });
        manager.SetTaskMarks(new HashSet<Guid> { id }, new Dictionary<Guid, TaskTimer> { [id] = timer });

        Assert.Equal(1, pulses);
    }

    [Fact]
    public void ClearAttention_removes_an_ask()
    {
        var manager = Started();
        StandOn(manager, "Deskbar");
        var other = IdOf(manager, "iam.ge");
        Assert.True(manager.RequestAttention(other, CSharpFunctionalExtensions.Maybe<WindowHandle>.None).Value);
        Assert.True(Group(manager, "iam.ge").WantsAttention);

        Assert.True(manager.ClearAttention(other));

        Assert.Empty(manager.AskingWorkspaces);
        Assert.False(Group(manager, "iam.ge").WantsAttention);
        Assert.False(manager.ClearAttention(other));
    }
}
