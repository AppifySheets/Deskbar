using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// When the bar announces its strip to the tasks panel (BarStripNotices; final review of the tasks panel branch,
// Important 2). The panel re-registers behind the bar on every notice, so a missing notice leaves the bar inside the
// panel, and a notice too many lets the two app bars re-register each other for ever. Each rule of the decision is one
// case here; FloatingBar calls the same function at every point it decides.
public class BarStripNoticesTests
{
    static readonly WindowRect Left = new(-1920, 0, 0, 1080);
    static readonly WindowRect Main = new(0, 0, 3840, 2160);
    static readonly WindowRect Other = new(3840, 0, 5760, 1080);

    static BarStripEvent.Docked Dock(string edge = "right", WindowRect? screen = null, bool fresh = false, int middle = 3700,
        Maybe<WindowRect> seamRight = default) =>
        new(edge, screen ?? Main, seamRight, middle, fresh);

    static BarStripEvent.Floating Floats(WindowRect? screen = null, int? middle = null) =>
        new(screen is null ? Maybe<WindowRect>.None : screen, middle is { } m ? m : Maybe<int>.None);

    static readonly BarStripEvent Gone = BarStripEvent.Gone.Instance;

    // The notices the bar sends for a run of events, in order, each judged against the last one SENT (as FloatingBar
    // keeps lastStrip only when it announces).
    static IReadOnlyList<BarStrip> Run(params BarStripEvent[] events) =>
        events.Aggregate((Last: (BarStrip?)null, Sent: ImmutableList<BarStrip>.Empty), (state, happened) =>
                BarStripNotices.Next(state.Last, happened) is { HasValue: true } next
                    ? (next.Value, state.Sent.Add(next.Value))
                    : state)
            .Sent;

    [Fact]
    public void The_first_dock_ever_announces_even_without_a_fresh_registration()
    {
        var sent = Run(Dock(fresh: false));
        Assert.Equal([new BarStrip("right", Main, Maybe<WindowRect>.None, 3700, Gone: false)], sent);
    }

    [Fact]
    public void The_first_floating_notice_ever_announces() =>
        Assert.Equal([new BarStrip(null, Main, Maybe<WindowRect>.None, 500, Gone: false)], Run(Floats(Main, 500)));

    // The bar never said where it was, so the panel has no place to give back: nothing to say.
    [Fact]
    public void Gone_before_anything_was_announced_says_nothing() => Assert.Empty(Run(Gone));

    // The dock guard, Explorer restarting, a dock after undock or minimize: the bar registered afresh at the place it
    // already had, and the panel must register behind it again.
    [Fact]
    public void A_fresh_registration_at_the_same_place_announces() =>
        Assert.Equal(2, Run(Dock(fresh: true), Dock(fresh: true)).Count);

    // THE LOOP BREAKER: the shell's re-ask and the end of a width drag re-dock in place without registering. Announced, the
    // panel would re-register, the shell would re-ask the bar, and so on for ever. A width drag moves the middle, which
    // does not count as another place.
    [Fact]
    public void A_re_dock_at_the_same_place_without_a_registration_says_nothing() =>
        Assert.Single(Run(Dock(fresh: true), Dock(fresh: false), Dock(fresh: false, middle: 3600)));

    // A display change can move the docked bar while its registration stands.
    [Fact]
    public void Another_edge_announces_without_a_registration() =>
        Assert.Equal(["right", "left"], Run(Dock("right", fresh: true), Dock("left")).Select(n => n.Edge));

    [Fact]
    public void Another_screen_announces_without_a_registration() =>
        Assert.Equal([Main, Other], Run(Dock(fresh: true), Dock(screen: Other)).Select(n => n.Screen));

    // A seam dock with the right-hand screen swapped is another place, though the edge and the left-hand screen match.
    [Fact]
    public void Another_right_hand_screen_of_the_seam_announces_without_a_registration()
    {
        var sent = Run(Dock("seam", Left, fresh: true, seamRight: Main), Dock("seam", Left, seamRight: Other));
        Assert.Equal([Main, Other], sent.Select(n => n.SeamRight.Value));
    }

    // Undocking: the panel docks by the floating rule, once. A second show that still floats is not news.
    [Fact]
    public void Floating_after_docked_announces_once_and_not_twice()
    {
        var sent = Run(Dock(fresh: true), Floats(Main, 1000), Floats(Main, 2000));
        Assert.Equal(2, sent.Count);
        Assert.Equal(new BarStrip(null, Main, Maybe<WindowRect>.None, 1000, Gone: false), sent[1]);
    }

    // Gone after docked and gone after floating both announce, from the last notice's screen and middle, with no edge
    // and no seam; a second gone in a row says nothing.
    [Fact]
    public void Gone_announces_once_whatever_came_before()
    {
        var afterDock = Run(Dock("seam", Left, fresh: true, middle: -20, seamRight: Main), Gone, Gone);
        Assert.Equal(2, afterDock.Count);
        Assert.Equal(new BarStrip(null, Left, Maybe<WindowRect>.None, -20, Gone: true), afterDock[1]);

        var afterFloat = Run(Floats(Main, 1000), Gone, Gone);
        Assert.Equal(2, afterFloat.Count);
        Assert.Equal(new BarStrip(null, Main, Maybe<WindowRect>.None, 1000, Gone: true), afterFloat[1]);
    }

    // A restore after minimize: whether it docks (at the very place it had, even without a fresh registration) or floats,
    // the panel hears it, since the last notice took the panel away.
    [Fact]
    public void Coming_back_after_gone_announces_again()
    {
        Assert.Equal(3, Run(Dock(fresh: true), Gone, Dock(fresh: false)).Count);
        Assert.Equal(3, Run(Floats(Main, 1000), Gone, Floats(Main, 1000)).Count);
    }

    // The window could not be read: the last notice's screen and middle stand in; with neither, no place, no notice.
    [Fact]
    public void A_floating_bar_whose_window_cannot_be_read_falls_back_on_the_last_notice()
    {
        var sent = Run(Dock("left", Left, fresh: true, middle: -1800), Floats());
        Assert.Equal(new BarStrip(null, Left, Maybe<WindowRect>.None, -1800, Gone: false), sent[1]);

        Assert.Equal(new BarStrip(null, Main, Maybe<WindowRect>.None, 1920, Gone: false), Assert.Single(Run(Floats(Main))));
        Assert.Empty(Run(Floats()));
    }
}
