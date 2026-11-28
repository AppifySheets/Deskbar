using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The tasks panel's keys, the pure half (PanelKeys, Deskbar task b61f79de): which line the arrows select, and what the
// keys card lists in each place.
public class PanelKeysTests
{
    static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
    static readonly IReadOnlyList<Guid> Shown = [A, B, C];

    [Fact]
    public void Down_moves_to_the_next_line_and_stops_at_the_last()
    {
        Assert.Equal(B, PanelKeys.Step(Shown, A, PanelKeys.Next).Value);
        Assert.Equal(C, PanelKeys.Step(Shown, C, PanelKeys.Next).Value);
    }

    [Fact]
    public void Up_moves_to_the_previous_line_and_stops_at_the_first()
    {
        Assert.Equal(A, PanelKeys.Step(Shown, B, PanelKeys.Previous).Value);
        Assert.Equal(A, PanelKeys.Step(Shown, A, PanelKeys.Previous).Value);
    }

    [Fact]
    public void Home_and_End_go_to_the_ends()
    {
        Assert.Equal(A, PanelKeys.Step(Shown, B, PanelKeys.First).Value);
        Assert.Equal(C, PanelKeys.Step(Shown, B, PanelKeys.Last).Value);
    }

    [Fact]
    public void With_nothing_shown_selected_Down_picks_the_first_and_Up_the_last()
    {
        Assert.Equal(A, PanelKeys.Step(Shown, Maybe<Guid>.None, PanelKeys.Next).Value);
        Assert.Equal(C, PanelKeys.Step(Shown, Maybe<Guid>.None, PanelKeys.Previous).Value);
        // A selection the view does not draw (another tab, a filter) counts as none.
        Assert.Equal(A, PanelKeys.Step(Shown, Guid.NewGuid(), PanelKeys.Next).Value);
    }

    [Fact]
    public void An_empty_view_steps_nowhere() =>
        Assert.True(PanelKeys.Step([], A, PanelKeys.Next).HasNoValue);

    [Fact]
    public void The_card_lists_keys_only_where_they_act()
    {
        Assert.Contains(new PanelKey("Enter", "Open the task's page"), PanelKeys.For(PanelPlace.InboxList));
        Assert.DoesNotContain(PanelKeys.For(PanelPlace.Stats), k => k.Keys == "Up / Down");
        Assert.DoesNotContain(PanelKeys.For(PanelPlace.InboxPage), k => k.Does == "Select the line above or below");
        Assert.Contains(PanelKeys.For(PanelPlace.InboxPage), k => k.Does == "Scroll the page");
        // Every place ends with the card's own key, so it is always where the eye lands last.
        Assert.All(Enum.GetValues<PanelPlace>(), place => Assert.Equal(new PanelKey("? or F1", "These keys"), PanelKeys.For(place)[^1]));
    }
}
