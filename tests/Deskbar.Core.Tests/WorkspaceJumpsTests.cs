using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// What a pinned workspace's item reads and where a click on it goes (WorkspaceJumps.Of). Petre: "i need to jump in
// and out of the messengers": outside the workspace the item goes in; inside, the same item goes back.
public class WorkspaceJumpsTests
{
    static readonly Workspace Deskbar = new(Guid.NewGuid(), "Deskbar", Guid.NewGuid());
    static readonly Workspace Notes = new(Guid.NewGuid(), "Notes", Guid.NewGuid()) { Pinned = true };
    static readonly Workspace Messengers = new(Guid.NewGuid(), "Messengers", Guid.NewGuid()) { Pinned = true };
    static readonly IReadOnlyList<Workspace> All = [Notes, Deskbar, Messengers];

    static WorkspaceJump JumpOf(IReadOnlyList<WorkspaceJump> jumps, Workspace pinned) => jumps.Single(j => j.PinnedId == pinned.Id);

    [Fact]
    public void Only_pinned_workspaces_get_an_item_in_list_order() =>
        Assert.Equal([Notes.Id, Messengers.Id], WorkspaceJumps.Of(All, Maybe<Guid>.None, Maybe<Workspace>.None).Select(j => j.PinnedId));

    [Fact]
    public void Outside_an_item_goes_in()
    {
        var jump = JumpOf(WorkspaceJumps.Of(All, Deskbar.Id, Notes), Messengers);

        Assert.Equal("Messengers", jump.Label);
        Assert.Equal(Messengers.Id, jump.Target.Value);
        Assert.False(jump.IsBack);
    }

    [Fact]
    public void Inside_the_item_flips_to_back()
    {
        var jump = JumpOf(WorkspaceJumps.Of(All, Messengers.Id, Deskbar), Messengers);

        Assert.Equal("↩ Deskbar", jump.Label);
        Assert.Equal(Deskbar.Id, jump.Target.Value);
        Assert.True(jump.IsBack);
    }

    [Fact]
    public void Inside_with_nothing_to_go_back_to_names_itself_and_goes_nowhere()
    {
        var jump = JumpOf(WorkspaceJumps.Of(All, Messengers.Id, Maybe<Workspace>.None), Messengers);

        Assert.Equal("Messengers", jump.Label);
        Assert.True(jump.Target.HasNoValue);
        Assert.False(jump.IsBack);
    }

    // No workspace is current on an unnamed desktop, so no item is "inside" and every one goes in.
    [Fact]
    public void On_an_unnamed_desktop_every_item_goes_in() =>
        Assert.All(WorkspaceJumps.Of(All, Maybe<Guid>.None, Deskbar),
            jump => Assert.Equal(jump.PinnedId, jump.Target.Value));

    // --- the previous workspace's item (WorkspaceJumps.BackItem) ------------------------------------------------------
    // Petre: "let's also have previous workspace available in strip". One "↩ <previous>" item, unless another item
    // already goes there.

    static readonly Workspace Mail = new(Guid.NewGuid(), "Mail", Guid.NewGuid());

    [Fact]
    public void The_back_item_goes_to_the_previous_workspace()
    {
        var back = WorkspaceJumps.BackItem(WorkspaceJumps.Of(All, Deskbar.Id, Mail), Mail).Value;

        Assert.Equal("↩ Mail", back.Label);
        Assert.Equal(Mail.Id, back.Target.Value);
        Assert.True(back.IsBack);
    }

    // Inside Messengers its own item already reads "↩ Deskbar": no second item for the same place.
    [Fact]
    public void No_back_item_when_the_pinned_item_already_goes_back() =>
        Assert.True(WorkspaceJumps.BackItem(WorkspaceJumps.Of(All, Messengers.Id, Deskbar), Deskbar).HasNoValue);

    // Back to a pinned workspace: its own item already goes there.
    [Fact]
    public void No_back_item_when_a_pinned_item_goes_there() =>
        Assert.True(WorkspaceJumps.BackItem(WorkspaceJumps.Of(All, Deskbar.Id, Messengers), Messengers).HasNoValue);

    [Fact]
    public void No_back_item_with_nowhere_to_go() =>
        Assert.True(WorkspaceJumps.BackItem(WorkspaceJumps.Of(All, Deskbar.Id, Maybe<Workspace>.None), Maybe<Workspace>.None).HasNoValue);

    // Petre, with a strip showing "↩ messengers" next to "messengers": "only one". In a pinned workspace whose "back"
    // is another PINNED workspace, that workspace's own item already goes there, so this one names itself and goes
    // nowhere, as with nothing to go back to. It keeps its place, so nothing shifts on the strip or the taskbar.
    [Fact]
    public void Inside_with_back_to_another_pinned_workspace_the_item_names_itself_and_goes_nowhere()
    {
        var jumps = WorkspaceJumps.Of(All, Messengers.Id, Notes);
        var inside = JumpOf(jumps, Messengers);

        Assert.Equal("Messengers", inside.Label);
        Assert.True(inside.Target.HasNoValue);
        Assert.False(inside.IsBack);
        Assert.Single(jumps, j => j.Target.HasValue && j.Target.Value == Notes.Id);
        Assert.True(WorkspaceJumps.BackItem(jumps, Notes).HasNoValue);
    }
}
