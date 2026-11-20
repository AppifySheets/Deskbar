using Deskbar.Core.Arrange;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Arrange;

// What dropping a row on the bar's arrange mode does, and what the bar draws on the way there
// (docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "What a drop does"). All of it is a rule about
// the state, the dragged row, the row under the pointer and the zone of that row, so it is decided here
// without a window. Every test asserts both halves, the action and what the bar shows, because the line the
// user sees has to promise exactly what the release then does.
public class RowDropTests
{
    // Stored order: Mail, Code, [Work: Tickets, Reviews], Docs, Personal. Mail is optionally pinned.
    sealed record World(AppState State, Guid Mail, Guid Code, Guid Tickets, Guid Reviews, Guid Docs, Guid Personal, Guid Work)
    {
        public RowRef.Workspace Ws(Guid id) => new(id);
        public RowRef.Group Grp => new(Work);
    }

    static World Build(bool mailPinned = false, string groupName = "Work")
    {
        var work = Guid.NewGuid();
        var mail = new Workspace(Guid.NewGuid(), "Mail", Guid.NewGuid()) { Pinned = mailPinned };
        var code = new Workspace(Guid.NewGuid(), "Code", Guid.NewGuid());
        var tickets = new Workspace(Guid.NewGuid(), "Tickets", Guid.NewGuid()) { GroupId = work };
        var reviews = new Workspace(Guid.NewGuid(), "Reviews", Guid.NewGuid()) { GroupId = work };
        var docs = new Workspace(Guid.NewGuid(), "Docs", Guid.NewGuid());
        var personal = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        var state = AppState.Empty with
        {
            Workspaces = [mail, code, tickets, reviews, docs, personal],
            Groups = [new Group(work, groupName) { Color = "#112233" }],
        };
        return new World(state, mail.Id, code.Id, tickets.Id, reviews.Id, docs.Id, personal.Id, work);
    }

    static readonly DropAction NoAction = new DropAction.Nothing();
    static readonly DropShow NoShow = new DropShow.None();

    static void AssertNothing((DropAction Action, DropShow Show) plan)
    {
        Assert.Equal(NoAction, plan.Action);
        Assert.Equal(NoShow, plan.Show);
    }

    static IEnumerable<string> Names(AppState s) => s.Workspaces.Select(w => w.Name);

    [Fact]
    public void Top_quarter_of_an_ungrouped_row_places_before_it_ungrouped()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Code), DropZone.Before);

        Assert.Equal(new DropAction.Place(w.Personal, w.Ws(w.Code), false), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Code), false, false), show);
    }

    [Fact]
    public void Bottom_quarter_of_a_groups_last_member_joins_at_the_bottom()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Reviews), DropZone.After);

        Assert.Equal(new DropAction.Place(w.Personal, w.Ws(w.Reviews), true), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Reviews), true, true), show);
    }

    [Fact]
    public void Top_quarter_of_the_row_after_a_group_places_ungrouped_below_the_group()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Docs), DropZone.Before);

        Assert.Equal(new DropAction.Place(w.Personal, w.Ws(w.Docs), false), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Docs), false, false), show);
    }

    [Fact]
    public void Onto_an_ungrouped_row_groups_with_it()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Docs), DropZone.Onto);

        Assert.Equal(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), action);
        Assert.Equal(new DropShow.Highlight(w.Ws(w.Docs), "Group with Docs…"), show);
    }

    [Fact]
    public void Onto_a_grouped_row_joins_below_it()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Tickets), DropZone.Onto);

        Assert.Equal(new DropAction.Place(w.Personal, w.Ws(w.Tickets), true), action);
        Assert.Equal(new DropShow.Highlight(w.Ws(w.Tickets), null), show);
    }

    [Fact]
    public void Top_quarter_of_a_group_header_places_above_the_whole_group()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Grp, DropZone.Before);

        Assert.Equal(new DropAction.Place(w.Personal, w.Grp, false), action);
        Assert.Equal(new DropShow.Line(w.Grp, false, false), show);
    }

    [Theory]
    [InlineData(DropZone.Onto)]
    [InlineData(DropZone.After)]
    public void The_rest_of_a_group_header_joins_at_the_top(DropZone zone)
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Grp, zone);

        Assert.Equal(new DropAction.Place(w.Personal, w.Ws(w.Tickets), false), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Tickets), false, true), show);
    }

    [Fact]
    public void Empty_space_below_the_last_row_places_at_the_end()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Tickets), null, DropZone.End);

        Assert.Equal(new DropAction.Place(w.Tickets, w.Ws(w.Personal), true), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Personal), true, false), show);

        // Ungrouped: Tickets leaves Work, which is left with one member and dissolves.
        var after = RowDrop.Applied(w.State, action, null);
        Assert.Null(after.Workspaces.Single(x => x.Id == w.Tickets).GroupId);
        Assert.Equal(w.Personal, after.Workspaces[^2].Id);
        Assert.Equal(w.Tickets, after.Workspaces[^1].Id);
    }

    [Fact]
    public void Empty_space_below_a_group_that_is_last_places_below_the_whole_group()
    {
        var w = Build();
        // Move the group to the very end first: Work is then the last thing on the bar.
        var grouped = RowDrop.Applied(w.State, new DropAction.PlaceGroup(w.Work, w.Ws(w.Personal), true), null);
        var (action, show) = RowDrop.Plan(grouped, w.Ws(w.Personal), null, DropZone.End);

        Assert.Equal(new DropAction.Place(w.Personal, w.Grp, true), action);
        Assert.Equal(new DropShow.Line(w.Grp, true, false), show);
    }

    [Fact]
    public void A_member_reorders_inside_its_own_group()
    {
        var w = Build();
        var (action, show) = RowDrop.Plan(w.State, w.Ws(w.Reviews), w.Ws(w.Tickets), DropZone.Before);

        Assert.Equal(new DropAction.Place(w.Reviews, w.Ws(w.Tickets), false), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Tickets), false, true), show);

        var after = RowDrop.Applied(w.State, action, null);
        Assert.Equal(["Mail", "Code", "Reviews", "Tickets", "Docs", "Personal"], Names(after));
        Assert.All(after.Workspaces.Where(x => x.Name is "Reviews" or "Tickets"), x => Assert.Equal(w.Work, x.GroupId));
    }

    [Fact]
    public void Onto_itself_or_a_row_of_its_own_group_does_nothing()
    {
        var w = Build();
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Tickets), w.Ws(w.Reviews), DropZone.Onto));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Docs), w.Ws(w.Docs), DropZone.Onto));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Docs), w.Ws(w.Docs), DropZone.Before));
    }

    [Fact]
    public void A_drop_that_leaves_the_order_as_it_is_does_nothing()
    {
        var w = Build();
        // Docs is already directly before Personal, and Tickets already first in its group.
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Docs), w.Ws(w.Personal), DropZone.Before));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Tickets), w.Ws(w.Tickets), DropZone.After));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Tickets), w.Grp, DropZone.Onto));
    }

    [Fact]
    public void A_header_moves_among_top_level_rows_only()
    {
        var w = Build();
        Enum.GetValues<DropZone>().ToList().ForEach(zone =>
            AssertNothing(RowDrop.Plan(w.State, w.Grp, w.Ws(w.Tickets), zone)));

        var (action, show) = RowDrop.Plan(w.State, w.Grp, w.Ws(w.Docs), DropZone.After);
        Assert.Equal(new DropAction.PlaceGroup(w.Work, w.Ws(w.Docs), true), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Docs), true, false), show);
    }

    [Fact]
    public void A_header_cannot_be_dropped_onto_a_row()
    {
        var w = Build();
        AssertNothing(RowDrop.Plan(w.State, w.Grp, w.Ws(w.Docs), DropZone.Onto));
        AssertNothing(RowDrop.Plan(w.State, w.Grp, w.Grp, DropZone.Before));
    }

    [Fact]
    public void A_pinned_row_moves_only_among_pinned_rows()
    {
        var w = Build(mailPinned: true);
        var code = w.Ws(w.Code);

        // Mail is the only pinned row, so there is no other pinned row to go beside; every try is a no-op or refused.
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Mail), code, DropZone.After));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Mail), w.Ws(w.Reviews), DropZone.After));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Mail), w.Grp, DropZone.Before));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Mail), null, DropZone.End));

        // An unpinned row cannot go beside a pinned one either.
        AssertNothing(RowDrop.Plan(w.State, code, w.Ws(w.Mail), DropZone.Before));
    }

    [Fact]
    public void A_pinned_row_is_planned_beside_another_pinned_row()
    {
        var w = Build(mailPinned: true);
        var pinnedDocs = w.State with
        {
            Workspaces = w.State.Workspaces.Select(x => x.Id == w.Docs ? x with { Pinned = true } : x).ToList(),
        };
        var (action, show) = RowDrop.Plan(pinnedDocs, w.Ws(w.Docs), w.Ws(w.Mail), DropZone.Before);

        Assert.Equal(new DropAction.Place(w.Docs, w.Ws(w.Mail), false), action);
        Assert.Equal(new DropShow.Line(w.Ws(w.Mail), false, false), show);
    }

    [Fact]
    public void Nothing_groups_with_a_pinned_row()
    {
        var w = Build(mailPinned: true);
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Mail), DropZone.Onto));
        AssertNothing(RowDrop.Plan(w.State, w.Ws(w.Mail), w.Ws(w.Docs), DropZone.Onto));
    }

    [Fact]
    public void A_group_with_a_taken_name_is_still_planned()
    {
        // Planning does not look at names: the apply step refuses a taken name, with its usual message.
        var w = Build(groupName: "Docs");
        var (action, _) = RowDrop.Plan(w.State, w.Ws(w.Personal), w.Ws(w.Docs), DropZone.Onto);

        Assert.Equal(new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), action);
    }

    [Fact]
    public void Applied_place_leaves_and_dissolves_the_old_group()
    {
        var w = Build();
        var after = RowDrop.Applied(w.State, new DropAction.Place(w.Tickets, w.Ws(w.Docs), true), null);

        // Tickets now stands on its own right after Docs; Reviews was left alone in Work, so Work is gone.
        Assert.Equal(["Mail", "Code", "Reviews", "Docs", "Tickets", "Personal"], Names(after));
        Assert.Empty(after.Groups);
        Assert.All(after.Workspaces, x => Assert.Null(x.GroupId));
    }

    [Fact]
    public void Applied_place_joins_the_group_of_a_member_it_lands_beside()
    {
        var w = Build();
        var after = RowDrop.Applied(w.State, new DropAction.Place(w.Personal, w.Ws(w.Tickets), true), null);

        Assert.Equal(["Mail", "Code", "Tickets", "Personal", "Reviews", "Docs"], Names(after));
        Assert.Equal(w.Work, after.Workspaces.Single(x => x.Id == w.Personal).GroupId);
    }

    [Fact]
    public void Applied_place_beside_a_group_goes_before_its_first_or_after_its_last_member()
    {
        var w = Build();
        var before = RowDrop.Applied(w.State, new DropAction.Place(w.Personal, w.Grp, false), null);
        var below = RowDrop.Applied(w.State, new DropAction.Place(w.Mail, w.Grp, true), null);

        Assert.Equal(["Mail", "Code", "Personal", "Tickets", "Reviews", "Docs"], Names(before));
        Assert.Null(before.Workspaces.Single(x => x.Id == w.Personal).GroupId);
        Assert.Equal(["Code", "Tickets", "Reviews", "Mail", "Docs", "Personal"], Names(below));
    }

    [Fact]
    public void Applied_group_with_puts_target_first_at_its_place()
    {
        var w = Build();
        var after = RowDrop.Applied(w.State, new DropAction.GroupWith(w.Docs, w.Personal, "Docs"), "  Reading ");

        // Target first, dragged second, both where Docs was; the name is trimmed and the colour is the target's.
        Assert.Equal(["Mail", "Code", "Tickets", "Reviews", "Docs", "Personal"], Names(after));
        var group = after.Groups.Single(g => g.Name == "Reading");
        Assert.Equal(group.Id, after.Workspaces.Single(x => x.Id == w.Docs).GroupId);
        Assert.Equal(group.Id, after.Workspaces.Single(x => x.Id == w.Personal).GroupId);
        Assert.Equal(2, after.Groups.Count);
    }

    [Fact]
    public void Applied_group_with_takes_the_targets_colour_and_moves_a_member_out_of_its_group()
    {
        var w = Build();
        var coloured = w.State with
        {
            Workspaces = w.State.Workspaces.Select(x => x.Id == w.Code ? x with { Color = "#AABBCC" } : x).ToList(),
        };
        var after = RowDrop.Applied(coloured, new DropAction.GroupWith(w.Code, w.Tickets, "Code"), "Code stuff");

        Assert.Equal("#AABBCC", after.Groups.Single(g => g.Name == "Code stuff").Color);
        // Tickets left Work, which dissolved: Reviews stands alone again.
        Assert.DoesNotContain(after.Groups, g => g.Id == w.Work);
        Assert.Null(after.Workspaces.Single(x => x.Id == w.Reviews).GroupId);
    }

    [Fact]
    public void Applied_place_group_keeps_member_order()
    {
        var w = Build();
        var after = RowDrop.Applied(w.State, new DropAction.PlaceGroup(w.Work, w.Ws(w.Mail), false), null);

        Assert.Equal(["Tickets", "Reviews", "Mail", "Code", "Docs", "Personal"], Names(after));
        Assert.Equal(w.State.Groups, after.Groups);

        var end = RowDrop.Applied(w.State, new DropAction.PlaceGroup(w.Work, w.Ws(w.Personal), true), null);
        Assert.Equal(["Mail", "Code", "Docs", "Personal", "Tickets", "Reviews"], Names(end));
    }

    [Fact]
    public void Applied_pinned_place_keeps_unpinned_slots()
    {
        // Stored: Mail(p), Code, Docs(p), Personal. Docs goes before Mail: the two pinned rows swap their
        // slots while Code and Personal keep theirs, the way a move among the pinned rows already does.
        var w = Build(mailPinned: true);
        var state = w.State with
        {
            Workspaces =
            [
                w.State.Workspaces.Single(x => x.Id == w.Mail),
                w.State.Workspaces.Single(x => x.Id == w.Code),
                w.State.Workspaces.Single(x => x.Id == w.Docs) with { Pinned = true },
                w.State.Workspaces.Single(x => x.Id == w.Personal),
            ],
            Groups = [],
        };
        var after = RowDrop.Applied(state, new DropAction.Place(w.Docs, w.Ws(w.Mail), false), null);

        Assert.Equal(["Docs", "Code", "Mail", "Personal"], Names(after));
    }

    // A drop that MERGES blocks (one fewer on its side) must not move where a pinned row returns when it is
    // unpinned: the slot the dragged row vacated goes, not the side's last one.
    static (AppState State, Guid A, Guid B, Guid P, Guid C, Guid X, Guid Y, Guid G) PinnedBetween()
    {
        var g = Guid.NewGuid();
        var a = new Workspace(Guid.NewGuid(), "A", Guid.NewGuid());
        var b = new Workspace(Guid.NewGuid(), "B", Guid.NewGuid());
        var p = new Workspace(Guid.NewGuid(), "P", Guid.NewGuid()) { Pinned = true };
        var c = new Workspace(Guid.NewGuid(), "C", Guid.NewGuid());
        var x = new Workspace(Guid.NewGuid(), "X", Guid.NewGuid()) { GroupId = g };
        var y = new Workspace(Guid.NewGuid(), "Y", Guid.NewGuid()) { GroupId = g };
        var state = AppState.Empty with { Workspaces = [a, b, p, c, x, y], Groups = [new Group(g, "G")] };
        return (state, a.Id, b.Id, p.Id, c.Id, x.Id, y.Id, g);
    }

    [Fact]
    public void Applied_grouping_above_a_pinned_row_keeps_its_return_place()
    {
        var t = PinnedBetween();
        var after = RowDrop.Applied(t.State, new DropAction.GroupWith(t.A, t.B, "AB"), null);

        Assert.Equal(["A", "B", "P", "C", "X", "Y"], Names(after));
    }

    [Fact]
    public void Applied_joining_a_group_above_a_pinned_row_keeps_its_return_place()
    {
        var t = PinnedBetween();
        var after = RowDrop.Applied(t.State, new DropAction.Place(t.B, new RowRef.Workspace(t.X), true), null);

        Assert.Equal(["A", "P", "C", "X", "B", "Y"], Names(after));
    }

    [Fact]
    public void Applied_nothing_returns_the_state_unchanged()
    {
        var w = Build();
        Assert.Same(w.State, RowDrop.Applied(w.State, new DropAction.Nothing(), null));
    }
}
