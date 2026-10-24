using System.Windows;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// Where an icon glides FROM when rows expand or collapse. A pinned window is on the bar more than once: in the
// pinned row and in every expanded row's titled list. Petre, with two rows expanded and one collapsing: "pinned
// beeper flies into the expanded bar again", "it shouldn't", "it's already there". The old snapshot was keyed by
// window alone, so every copy started from the first one recorded.
public class FloatingBarGlideTests
{
    static readonly WindowHandle Beeper = new(202);
    static readonly WindowHandle Editor = new(101);

    // Before the collapse: Beeper in the pinned row and in two expanded rows, the editor only in its own row.
    static FloatingBar.LayoutSnapshot TwoRowsExpanded() => new(
        Rows: [],
        Icons: new()
        {
            [("pinned", Beeper)] = new Point(10, 5),
            [("workspace:a", Beeper)] = new Point(10, 80),
            [("workspace:b", Beeper)] = new Point(10, 200),
            [("workspace:b", Editor)] = new Point(10, 180),
        },
        FirstSeen: new()
        {
            [Beeper] = new Point(10, 5),
            [Editor] = new Point(10, 180),
        });

    [Fact]
    public void A_pinned_window_in_a_row_that_stays_expanded_starts_from_its_own_copy() =>
        Assert.Equal(new Point(10, 200), FloatingBar.WhereItWas(TwoRowsExpanded(), "workspace:b", Beeper));

    [Fact]
    public void The_pinned_rows_own_copy_starts_from_itself() =>
        Assert.Equal(new Point(10, 5), FloatingBar.WhereItWas(TwoRowsExpanded(), "pinned", Beeper));

    // A row expanding for the first time has no copy of Beeper yet, so it still flies in from where Beeper was.
    [Fact]
    public void A_pinned_window_new_to_a_row_flies_in_from_where_it_was_first_seen() =>
        Assert.Equal(new Point(10, 5), FloatingBar.WhereItWas(TwoRowsExpanded(), "workspace:c", Beeper));

    [Fact]
    public void A_window_that_was_not_on_the_bar_does_not_glide() =>
        Assert.Null(FloatingBar.WhereItWas(TwoRowsExpanded(), "workspace:b", new WindowHandle(303)));
}
