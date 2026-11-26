using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;

namespace Deskbar.Windows.Tests;

// Quick find's window and its controller (docs/superpowers/specs/2026-11-04-quick-find-design.md). The window
// is display only; the controller decides what is listed and what Enter does. Built on an STA thread like
// every other WPF window test here.
public class QuickFindWindowTests
{
    static readonly Guid Accounting = Guid.NewGuid();

    static FindResult Window(string key, string title, nint hwnd) =>
        new(key, FindKind.Window, title, new FindPlace(Accounting, "Accounting"), $"{title} Accounting", Window: new WindowHandle(hwnd));

    static QuickFind Controller(QuickFindWindow window, IReadOnlyList<FindResult> catalog, Func<FindResult, Result> go) =>
        new(window, () => catalog, () => [Accounting], go, _ => "#5B6CFF", _ => null);

    [Fact]
    public void Typing_lists_matches_and_highlights_the_first() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice 2026-10", 1), Window("w:2", "Notes", 2)], _ => Result.Success());

        find.Show("inv");
        find.Settle();

        Assert.Equal(["w:1"], window.Keys);
        Assert.Equal("w:1", window.Highlighted);
        window.Dismiss();
    });

    [Fact]
    public void Escape_dismisses_and_enter_chooses_the_highlighted_key() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var went = new List<string>();
        using var find = Controller(window, [Window("w:1", "Invoice a", 1), Window("w:2", "Invoice b", 2)], r => { went.Add(r.Key); return Result.Success(); });

        find.Show("inv");
        window.Press(Key.Down);
        window.Press(Key.Enter);
        Assert.Equal(["w:2"], went);
        Assert.False(window.IsOpen);

        find.Show("inv");
        window.Press(Key.Escape);
        Assert.False(window.IsOpen);
        Assert.Single(went);
    });

    // The plan's Review Focus, item 1: the window closed between the search and Enter.
    [Fact]
    public void Going_to_a_closed_window_fails_without_switching() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice a", 1), Window("w:2", "Invoice b", 2)],
            r => r.Key == "w:1" ? Result.Failure("That window has closed.") : Result.Success());

        find.Show("inv");
        window.Press(Key.Enter);

        Assert.True(window.IsOpen);
        Assert.Equal(["w:2"], window.Keys);
        Assert.Equal("w:2", window.Highlighted);
        window.Dismiss();
    });

    // Final review, finding 1: Alt+F4 closed the box for good, and the next shortcut press threw on a closed
    // window and took the app down. Closing it must only hide it.
    [Fact]
    public void Closing_the_box_hides_it_and_it_opens_again() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.Show("inv");
        window.Close();
        Assert.False(window.IsOpen);

        find.Show("inv");
        find.Settle();
        Assert.True(window.IsOpen);
        Assert.Equal(["w:1"], window.Keys);
        window.Dismiss();
    });

    // The bar's Search button (QuickFind.ToggleFromBar).
    [Fact]
    public void The_bars_button_opens_the_box() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.ToggleFromBar();

        Assert.True(window.IsOpen);
        window.Dismiss();
    });

    // The press on the bar closes the open box (Deactivated) before the click arrives; the click must not open it again.
    [Fact]
    public void A_second_click_on_the_bars_button_closes_the_box() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.ToggleFromBar();
        window.Dismiss(); // what Deactivated does
        find.ToggleFromBar();

        Assert.False(window.IsOpen);
    });

    [Fact]
    public void The_bars_button_opens_it_again_later() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.ToggleFromBar();
        window.Dismiss();
        Thread.Sleep(600);
        find.ToggleFromBar();

        Assert.True(window.IsOpen);
        window.Dismiss();
    });

    // Finding 6: after the text changes, the highlight goes to the new top result; it is kept by key only
    // across background refreshes.
    [Fact]
    public void Typing_moves_the_highlight_to_the_new_top_result() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice a", 1), Window("w:2", "Invoice b", 2)], _ => Result.Success());

        find.Show("inv");
        window.Press(Key.Down);
        Assert.Equal("w:2", window.Highlighted);

        window.Query.Text = "invoice";
        find.Settle();
        Assert.Equal("w:1", window.Highlighted);
        window.Dismiss();
    });

    // Finding 4: the once-a-second refresh must not rebuild rows that did not change (it cost a full redraw
    // and snapped a scrolled list back to the highlight every second)...
    [Fact]
    public void Refreshing_with_nothing_changed_keeps_the_rows() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.Show("inv");
        find.Settle();
        var drawn = window.Rebuilds;
        find.Tick();
        Assert.Equal(drawn, window.Rebuilds);
        window.Dismiss();
    });

    // ...and must not rebuild the catalogue at all while nothing is typed, since nothing is shown.
    [Fact]
    public void An_empty_box_does_not_rebuild_the_catalogue_each_second() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var built = 0;
        using var find = new QuickFind(window, () => { built++; return [Window("w:1", "Invoice", 1)]; }, () => [], _ => Result.Success(), _ => null, _ => null);

        find.Show("");
        find.Settle();
        var atOpen = built;
        find.Tick();
        Assert.Equal(atOpen, built);
        window.Dismiss();
    });

    // Findings 2 and 3: opening asks for fresh tab readings, and pulls the box to the front by its own handle,
    // because a `deskbar find` from a terminal arrives without the foreground right a hotkey press carries.
    [Fact]
    public void Opening_refreshes_tabs_and_brings_the_box_forward() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var refreshed = 0;
        nint brought = 0;
        using var find = new QuickFind(window, () => [], () => [], _ => Result.Success(), _ => null, _ => null,
            refresh: () => refreshed++, bringForward: hwnd => brought = hwnd);

        find.Show("");

        Assert.Equal(1, refreshed);
        Assert.Equal(find.EnsureHandle(), brought);
        window.Dismiss();
    });

    [Fact]
    public void Results_sit_under_their_workspace_header() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.Show("inv");
        find.Settle();

        Assert.Equal(["Accounting"], window.Headers);
        window.Dismiss();
    });

    // Task 684210af: "it takes several seconds to show text i've typed; let me type and tell me it's thinking or
    // waiting". The box opens before the catalogue is built, so the first letters typed after the hotkey land in it.
    [Fact]
    public void The_box_opens_before_the_catalogue_is_built() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var built = 0;
        using var find = new QuickFind(window, () => { built++; return [Window("w:1", "Invoice", 1)]; }, () => [], _ => Result.Success(), _ => null, _ => null);

        find.Show("inv");
        Assert.True(window.IsOpen);
        Assert.Equal(0, built);
        Assert.Equal(QuickFindWindow.SearchingText, window.Count.Text);

        find.Settle();
        Assert.Equal(1, built);
        Assert.Equal(["w:1"], window.Keys);
        Assert.Equal("1 result", window.Count.Text);
        window.Dismiss();
    });

    // A key only marks the search as waiting and says so; the rows stay those of the last search until it runs.
    [Fact]
    public void Typing_says_searching_until_the_search_runs() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1), Window("w:2", "Notes", 2)], _ => Result.Success());
        find.Show("inv");
        find.Settle();

        window.Query.Text = "no";
        Assert.Equal(QuickFindWindow.SearchingText, window.Count.Text);
        Assert.Equal(["w:1"], window.Keys);

        find.Settle();
        Assert.Equal(["w:2"], window.Keys);
        Assert.Equal("1 result", window.Count.Text);
        window.Dismiss();
    });

    // Keys typed while the UI thread was busy cost one search for the latest text, not one search each.
    [Fact]
    public void A_burst_of_keys_costs_one_search() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1), Window("w:2", "Notes", 2)], _ => Result.Success());
        find.Show("");
        find.Settle();
        var drawn = window.Rebuilds;

        window.Query.Text = "n";
        window.Query.Text = "no";
        window.Query.Text = "not";
        find.Settle();

        Assert.Equal(drawn + 1, window.Rebuilds);
        Assert.Equal(["w:2"], window.Keys);
        window.Dismiss();
    });

    // Enter right after typing, before the search ran, takes the top row for what is in the box, not for the text as
    // it was a moment earlier.
    [Fact]
    public void Enter_before_the_search_ran_takes_the_top_row_for_the_typed_text() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var went = new List<string>();
        using var find = Controller(window, [Window("w:1", "Invoice", 1), Window("w:2", "Notes", 2)], r => { went.Add(r.Key); return Result.Success(); });
        find.Show("inv");
        find.Settle();

        window.Query.Text = "no";
        window.Press(Key.Enter);

        Assert.Equal(["w:2"], went);
    });

    // A catalogue rebuild started just before a key holds that key, so the once-a-second refresh waits while keys arrive.
    [Fact]
    public void The_refresh_stands_aside_while_typing() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var built = 0;
        using var find = new QuickFind(window, () => { built++; return [Window("w:1", "Invoice", 1)]; }, () => [], _ => Result.Success(), _ => null, _ => null);
        find.Show("inv");
        find.Settle();
        find.Tick();
        Assert.Equal(2, built); // opening alone is not typing: the refresh runs

        window.Query.Text = "invo";
        find.Settle();
        var afterTyping = built;
        find.Tick();

        Assert.Equal(afterTyping, built);
        window.Dismiss();
    });
}
