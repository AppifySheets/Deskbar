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
}
