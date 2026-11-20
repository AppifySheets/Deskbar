using Deskbar.Core.Abstractions;
using Deskbar.Core.Arrange;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tests.Arrange;

// What the manager does with a drop RowDrop.Plan produced (docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md,
// "What a drop does"). The order rules live in RowDrop.Applied and have their own tests; these go through the
// manager to pin the part that is the manager's: asking for a group name, refusing a bad one or a stale target, and
// persisting exactly once (and not at all when nothing changes).
public class ApplyDropTests
{
    // Stored order: Mail, Code, [Work: Tickets, Reviews], Docs, Personal.
    sealed record World(WorkspaceManager Manager, FakeStore Store, Guid Mail, Guid Code, Guid Tickets, Guid Reviews, Guid Docs, Guid Personal, Guid Work)
    {
        public RowRef.Workspace Ws(Guid id) => new(id);
        public IEnumerable<string> Names => Manager.State.Workspaces.Select(w => w.Name);
    }

    static World Build(Func<Workspace, Workspace>? tweak = null)
    {
        var work = Guid.NewGuid();
        var t = tweak ?? (w => w);
        var mail = t(new Workspace(Guid.NewGuid(), "Mail", Guid.NewGuid()));
        var code = t(new Workspace(Guid.NewGuid(), "Code", Guid.NewGuid()));
        var tickets = new Workspace(Guid.NewGuid(), "Tickets", Guid.NewGuid()) { GroupId = work };
        var reviews = new Workspace(Guid.NewGuid(), "Reviews", Guid.NewGuid()) { GroupId = work };
        var docs = t(new Workspace(Guid.NewGuid(), "Docs", Guid.NewGuid()) { Color = "#445566" });
        var personal = t(new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid()));

        var desktops = new FakeDesktops();
        new[] { mail, code, tickets, reviews, docs, personal }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        var store = new FakeStore
        {
            Stored = AppState.Empty with
            {
                Workspaces = [mail, code, tickets, reviews, docs, personal],
                Groups = [new Group(work, "Work") { Color = "#112233" }],
            },
        };
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return new World(manager, store, mail.Id, code.Id, tickets.Id, reviews.Id, docs.Id, personal.Id, work);
    }

    static Maybe<string> Never(string _) => throw new InvalidOperationException("the name must not be asked for");

    [Fact]
    public void Placing_beside_a_top_level_row_leaves_the_old_group_in_one_persist()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        Assert.True(w.Manager.ApplyDrop(new DropAction.Place(w.Tickets, w.Ws(w.Docs), true), Never).IsSuccess);

        Assert.Equal(["Mail", "Code", "Reviews", "Docs", "Tickets", "Personal"], w.Names);
        Assert.Null(w.Manager.State.Workspaces.Single(x => x.Id == w.Tickets).GroupId);
        // Work was left with Reviews alone, so it dissolves.
        Assert.Empty(w.Manager.State.Groups);
        Assert.Null(w.Manager.State.Workspaces.Single(x => x.Id == w.Reviews).GroupId);
        Assert.Equal(saves + 1, w.Store.SaveCount);
    }

    [Fact]
    public void Placing_beside_a_member_joins_that_group_at_that_place()
    {
        var w = Build();

        Assert.True(w.Manager.ApplyDrop(new DropAction.Place(w.Personal, w.Ws(w.Reviews), false), Never).IsSuccess);

        Assert.Equal(["Mail", "Code", "Tickets", "Personal", "Reviews", "Docs"], w.Names);
        Assert.Equal(w.Work, w.Manager.State.Workspaces.Single(x => x.Id == w.Personal).GroupId);
    }

    [Fact]
    public void Placing_a_group_moves_its_members_together_in_order()
    {
        var w = Build();

        Assert.True(w.Manager.ApplyDrop(new DropAction.PlaceGroup(w.Work, w.Ws(w.Personal), true), Never).IsSuccess);

        Assert.Equal(["Mail", "Code", "Docs", "Personal", "Tickets", "Reviews"], w.Names);
    }

    [Fact]
    public void Grouping_two_rows_makes_a_group_at_the_targets_place_target_first()
    {
        var w = Build();
        var saves = w.Store.SaveCount;
        string? suggested = null;

        var result = w.Manager.ApplyDrop(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), s => { suggested = s; return "Docs"; });

        Assert.True(result.IsSuccess);
        Assert.Equal("Docs", suggested);
        var group = w.Manager.State.Groups.Single(g => g.Name == "Docs");
        Assert.Equal("#445566", group.Color);
        Assert.Equal(["Mail", "Code", "Tickets", "Reviews", "Docs", "Personal"], w.Names);
        Assert.Equal(["Docs", "Personal"], w.Manager.State.MembersOf(group.Id).Select(m => m.Name));
        Assert.Equal(saves + 1, w.Store.SaveCount);
    }

    [Fact]
    public void A_cancelled_name_changes_nothing()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        var result = w.Manager.ApplyDrop(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), _ => Maybe<string>.None);

        Assert.True(result.IsSuccess);
        Assert.Single(w.Manager.State.Groups);
        Assert.Equal(saves, w.Store.SaveCount);
    }

    [Fact]
    public void A_taken_group_name_is_refused_and_changes_nothing()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        var result = w.Manager.ApplyDrop(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), _ => "Work");

        Assert.True(result.IsFailure);
        Assert.Equal("A group named 'Work' already exists.", result.Error);
        Assert.Single(w.Manager.State.Groups);
        Assert.Equal(saves, w.Store.SaveCount);
    }

    [Fact]
    public void A_blank_group_name_is_refused()
    {
        var w = Build();

        var result = w.Manager.ApplyDrop(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), _ => "  ");

        Assert.True(result.IsFailure);
        Assert.Equal("Group name required", result.Error);
    }

    [Fact]
    public void A_dragged_member_of_another_group_leaves_it_when_grouping()
    {
        var w = Build();

        Assert.True(w.Manager.ApplyDrop(new DropAction.GroupWith(w.Docs, w.Tickets, "Docs"), _ => "Docs").IsSuccess);

        // Work lost Tickets and was left with Reviews alone, so it dissolves; the new group holds Docs and Tickets.
        Assert.DoesNotContain(w.Manager.State.Groups, g => g.Name == "Work");
        var group = w.Manager.State.Groups.Single();
        Assert.Equal(["Docs", "Tickets"], w.Manager.State.MembersOf(group.Id).Select(m => m.Name));
        Assert.Null(w.Manager.State.Workspaces.Single(x => x.Id == w.Reviews).GroupId);
    }

    [Fact]
    public void Nothing_is_a_no_op()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        Assert.True(w.Manager.ApplyDrop(new DropAction.Nothing(), Never).IsSuccess);

        Assert.Equal(saves, w.Store.SaveCount);
    }

    [Fact]
    public void A_drop_that_would_change_nothing_does_not_persist()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        // Docs placed before Personal is where Docs already is.
        Assert.True(w.Manager.ApplyDrop(new DropAction.Place(w.Docs, w.Ws(w.Personal), false), Never).IsSuccess);

        Assert.Equal(saves, w.Store.SaveCount);
    }

    // Mirrors ReorderedOnItsSide: Code and Personal are pinned, and swapping them takes their stored slots, so the
    // unpinned rows (Mail, Work, Docs) keep theirs and come back there when unpinned.
    [Fact]
    public void Placing_a_pinned_row_keeps_the_unpinned_rows_stored_places()
    {
        var w = Build();
        Assert.True(w.Manager.SetWorkspacePinned(w.Code, true).IsSuccess);
        Assert.True(w.Manager.SetWorkspacePinned(w.Personal, true).IsSuccess);

        Assert.True(w.Manager.ApplyDrop(new DropAction.Place(w.Personal, w.Ws(w.Code), false), Never).IsSuccess);

        Assert.Equal(["Mail", "Personal", "Tickets", "Reviews", "Docs", "Code"], w.Names);
    }

    [Fact]
    public void A_stale_drop_naming_a_gone_workspace_is_refused()
    {
        var w = Build();
        var saves = w.Store.SaveCount;

        var gone = w.Manager.ApplyDrop(new DropAction.Place(Guid.NewGuid(), w.Ws(w.Code), false), Never);
        var goneBeside = w.Manager.ApplyDrop(new DropAction.Place(w.Docs, w.Ws(Guid.NewGuid()), false), Never);
        var goneGroup = w.Manager.ApplyDrop(new DropAction.PlaceGroup(Guid.NewGuid(), w.Ws(w.Code), false), Never);

        Assert.Equal("Workspace no longer exists.", gone.Error);
        Assert.Equal("Workspace no longer exists.", goneBeside.Error);
        Assert.Equal("That group no longer exists.", goneGroup.Error);
        Assert.Equal(saves, w.Store.SaveCount);
    }
}
