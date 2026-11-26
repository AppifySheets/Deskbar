using System.Windows.Media;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.QuickFind;

namespace Deskbar.App;

// QUICK FIND'S CONTROLLER (docs/superpowers/specs/2026-11-04-quick-find-design.md): what the box lists and
// what Enter does. The window (QuickFindWindow) only draws.
//
// Everything it needs comes in as a function, so the composition root decides where the facts come from and
// the tests can hand in plain lists:
//   catalog   every result there is right now (open windows and tabs; launchable programs later)
//   recent    workspaces by recent use, for ranking ties
//   go        what Enter does to a result: go to its window, select its tab, or start it
//   colourOf  a place's lane colour, the same one the bar draws, or null for a place with none
//   iconOf    a result's icon
//
// While the box is open the catalogue is rebuilt every second, because tab readings arrive in the background
// (a full pass over every tab took 4.9 s on Petre's machine) and windows open and close. The highlight stays
// on its result through every rebuild (FindHighlight).
//
// TYPING NEVER WAITS FOR A SEARCH (task 684210af, Petre: "it takes several seconds to show text i've typed; let me
// type and tell me it's thinking or waiting"). Everything here runs on the app's one UI thread, which it shares
// with the bar, the tiles and every Claude session's hook calls, and the catalogue alone holds that thread for
// 100 to 400 ms (the overview query's IsPinned COM call measured 412 ms for 29 windows in the trace). It used to
// run in the hotkey handler before the box was even shown, again on every keystroke's redraw, and once a second
// on the timer, so letters typed meanwhile queued behind it. Now:
//   * the box opens first and the catalogue is built after, so the first letters land in the box at once;
//   * a keystroke only marks the search as pending and puts the count on "searching...". The search itself is
//     queued at Background priority, BELOW input and rendering, so every key already typed is taken and painted
//     first and one search then runs for the latest text (Settle). Several keys typed while the thread was busy
//     cost one search, not one each;
//   * the once-a-second refresh stands aside while keys are arriving (StandAside) and runs at Background
//     priority too;
//   * Enter, Up and Down settle a pending search first, so they act on the rows for what is in the box.
// A search that still takes long writes a "quick find:" line with where its time went to the trace (Measure).
public sealed class QuickFind : IDisposable
{
    readonly QuickFindWindow window;
    readonly Func<IReadOnlyList<FindResult>> catalog;
    readonly Func<IReadOnlyList<Guid>> recent;
    readonly Func<FindResult, Result> go;
    readonly Func<FindPlace, string?> colourOf;
    readonly Func<FindResult, ImageSource?> iconOf;
    readonly Action? refreshSources;
    readonly Action<nint>? bringForward;
    // Background priority so a tick queued behind keystrokes lets them through first (see the class comment).
    readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly IDisposable subscriptions;

    // How long after the last keystroke the once-a-second refresh keeps standing aside. A refresh started just
    // before a key holds that key for the whole catalogue build, so none starts while Petre is typing.
    static readonly TimeSpan StandAside = TimeSpan.FromSeconds(1.5);

    // A search waiting to run: the box's text it will search for (null when the text has not changed since the
    // last search), whether the catalogue must be built first (true after opening), and whether a Settle is
    // already queued on the dispatcher, so a burst of keys queues one. `pendingSince` is when the oldest of the
    // waiting changes happened, for the trace.
    string? pendingText;
    bool pendingCatalog;
    bool settleQueued;
    DateTime pendingSince;
    DateTime lastKeyAt = DateTime.MinValue;

    // Long enough to be worth a trace line: below it a search is not what makes typing feel slow.
    const long SlowMs = 100;

    static readonly TimeSpan JustClosed = TimeSpan.FromMilliseconds(500);
    DateTime dismissedAt = DateTime.MinValue;

    // Results whose Enter failed while the box was open: a window that closed between the search and Enter.
    // Dropped from the list so the next Enter takes the next row (the plan's Review Focus, item 1), and
    // forgotten when the box closes, since a failure now says nothing about the next time.
    readonly HashSet<string> gone = [];

    IReadOnlyList<FindResult> all = [];
    IReadOnlyList<FindResult> shown = [];
    FindQuery query = FindQuery.Parse("");

    //   refreshSources  asks the background tab reader to read stale windows again, without waiting for it
    //                   (final review, finding 3: tab titles otherwise only refreshed when the bar rebuilt)
    //   bringForward    pulls the box to the front by its own handle. A hotkey press carries the right to take
    //                   the foreground; a `deskbar find` typed in a terminal does not, and without this the box
    //                   would show with no keyboard focus (finding 2)
    public QuickFind(QuickFindWindow window, Func<IReadOnlyList<FindResult>> catalog, Func<IReadOnlyList<Guid>> recent,
        Func<FindResult, Result> go, Func<FindPlace, string?> colourOf, Func<FindResult, ImageSource?> iconOf,
        Action? refresh = null, Action<nint>? bringForward = null)
    {
        this.window = window;
        this.catalog = catalog;
        this.recent = recent;
        this.go = go;
        this.colourOf = colourOf;
        this.iconOf = iconOf;
        refreshSources = refresh;
        this.bringForward = bringForward;

        timer.Tick += (_, _) => Tick();
        // Up, Down and Enter act on the rows for the text in the box, so a search still waiting runs first.
        window.Settling = Settle;
        subscriptions = new System.Reactive.Disposables.CompositeDisposable(
            window.Typed.Subscribe(text =>
            {
                lastKeyAt = DateTime.UtcNow;
                Defer(text, catalogue: false);
            }),
            window.Chosen.Subscribe(Choose),
            window.Dismissed.Subscribe(_ =>
            {
                timer.Stop();
                gone.Clear();
                // A search queued for the box that just closed has nothing to draw into. The results go with the box
                // (the window clears its rows too): the next open builds its rows after showing, and a click in the
                // meantime must not land on a result from last time whose window may have closed since.
                (pendingText, pendingCatalog) = (null, false);
                shown = [];
                dismissedAt = DateTime.UtcNow;
            }));
    }

    // The window's handle, so the composition root can hand it to WindowMonitor.Ignore before it ever shows:
    // the monitor lists the app's own windows, and quick find must never find itself.
    public nint EnsureHandle() => new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

    // Opens the box, or brings it back to the front with the text replaced when it is already open.
    //
    // The box opens BEFORE the catalogue is built: building it holds the UI thread for a few hundred ms, and while
    // the box was not yet showing, the first letters typed after Win+Alt+Space had nowhere to go.
    public void Show(string text = "")
    {
        gone.Clear();
        refreshSources?.Invoke();
        window.Open(text);
        // Open's own change to the text is not Petre typing, so it must not hold off the once-a-second refresh.
        lastKeyAt = DateTime.MinValue;
        bringForward?.Invoke(EnsureHandle());
        // Open set the box's text, which already queued the search through Typed when it differed; this makes sure
        // the catalogue is rebuilt as well, and that the search runs when the text was the same as last time.
        Defer(text, catalogue: true);
        timer.Start();
    }

    // Marks a search as waiting, puts the count on "searching..." and queues one Settle at Background priority, below
    // input and rendering: the keys already typed are taken and painted before it runs, and it searches whatever the
    // box holds by then.
    void Defer(string text, bool catalogue)
    {
        if (pendingText is null && !pendingCatalog) pendingSince = DateTime.UtcNow;
        pendingText = text;
        pendingCatalog |= catalogue;
        window.ShowSearching();
        if (settleQueued) return;
        settleQueued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Settle);
    }

    // Runs the waiting search now, if there is one: from the dispatcher once the keys have been taken, and from
    // Up, Down and Enter so they act on the rows for what is typed. The tests call it to finish what typing started.
    internal void Settle()
    {
        settleQueued = false;
        if ((pendingText is null && !pendingCatalog) || !window.IsOpen) return;
        var waited = (long)(DateTime.UtcNow - pendingSince).TotalMilliseconds;
        var rebuild = pendingCatalog;
        (pendingText, pendingCatalog) = (null, false);
        // The query is read from the box AFTER the catalogue is built, not copied before it: catalog() makes COM calls,
        // which pump messages on this STA thread, so a key can arrive in the middle and even run a nested Settle. Taking
        // the box's text as it stands then keeps this search from drawing older text over the nested one's rows.
        Measure(rebuild, typed: true, waited, () => query = FindQuery.Parse(window.Query.Text));
    }

    // The bar's Search button. A press on the bar takes the foreground, so an open box has already closed itself
    // (Deactivated) by the time the click arrives, and showing it again would make a second click reopen it. A box
    // that closed this recently is the same press closing it, so the click stops there, as Start's button does.
    public void ToggleFromBar()
    {
        if (DateTime.UtcNow - dismissedAt < JustClosed) return;
        Show();
    }

    // The once-a-second refresh while the box is open. Nothing typed means nothing is shown, so the catalogue
    // (one overview query, two COM calls per window) is not rebuilt for an empty box (finding 4).
    //
    // It also stands aside while Petre types (StandAside) and while a search is waiting, which carries a fresh
    // catalogue of its own when the box has just opened: a rebuild started just before a key holds that key.
    internal void Tick()
    {
        if (query.IsEmpty || pendingText is not null || pendingCatalog || DateTime.UtcNow - lastKeyAt < StandAside) return;
        refreshSources?.Invoke();
        Measure(rebuild: true, typed: false, waited: 0, () => { });
    }

    // One search, timed: the catalogue when `rebuild` asks for it, then ranking and drawing. Written to the trace when
    // it took long enough to be felt, with what it was for and how long it waited after the key, so the next "typing
    // is slow" is read off a line (CLAUDE.md: measure, do not theorise). `prepare` sets the query before ranking.
    void Measure(bool rebuild, bool typed, long waited, Action prepare)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (rebuild) all = catalog();
        var catalogMs = clock.ElapsedMilliseconds;
        prepare();
        var (rankMs, rows) = Render(typed);
        var totalMs = clock.ElapsedMilliseconds;
        if (totalMs >= SlowMs || waited >= SlowMs)
            ClickTrace.Write($"quick find: {(typed ? "search" : "refresh")} took {totalMs}ms (catalogue {(rebuild ? $"{catalogMs}ms for {all.Count}" : "kept")}, " +
                             $"rank {rankMs}ms, draw {totalMs - catalogMs - rankMs}ms for {rows} rows), waited {waited}ms after the key");
    }

    // Ranks and draws, returning how long ranking took (the rest of the time is the draw, icons included) and the
    // number of rows drawn, for Measure's trace line.
    (long RankMs, int Rows) Render(bool typed)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        shown = FindRanking.Rank(query, all.Where(r => !gone.Contains(r.Key)).ToList(), recent());
        var rankMs = clock.ElapsedMilliseconds;
        var groups = FindGrouping.Group(shown)
            .Select(g => new FindGroupView(g.Header, colourOf(g.Place), !g.Open,
                g.Results.Select(r => new FindRowView(r.Key, iconOf(r), r.Title, r.Detail)).ToList()))
            .ToList();
        window.Present(groups, query.Pieces, typed);
        // A key that arrived while this ran (the catalogue's COM calls pump messages) has a search of its own queued:
        // until it runs, the count keeps saying so rather than giving a number for the older text.
        if (pendingText is not null || pendingCatalog) window.ShowSearching();
        return (rankMs, shown.Count);
    }

    void Choose(string key)
    {
        if (shown.FirstOrDefault(r => r.Key == key) is not { } result) return;
        go(result).Match(
            window.Dismiss,
            _ =>
            {
                gone.Add(key);
                Render(typed: false);
            });
    }

    public void Dispose()
    {
        timer.Stop();
        subscriptions.Dispose();
        window.CloseForGood();
    }
}
