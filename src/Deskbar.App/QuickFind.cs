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
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly IDisposable subscriptions;

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
        subscriptions = new System.Reactive.Disposables.CompositeDisposable(
            window.Typed.Subscribe(text => { query = FindQuery.Parse(text); Render(typed: true); }),
            window.Chosen.Subscribe(Choose),
            window.Dismissed.Subscribe(_ => { timer.Stop(); gone.Clear(); }));
    }

    // The window's handle, so the composition root can hand it to WindowMonitor.Ignore before it ever shows:
    // the monitor lists the app's own windows, and quick find must never find itself.
    public nint EnsureHandle() => new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

    // Opens the box, or brings it back to the front with the text replaced when it is already open.
    public void Show(string text = "")
    {
        gone.Clear();
        refreshSources?.Invoke();
        all = catalog();
        query = FindQuery.Parse(text);
        window.Open(text);
        bringForward?.Invoke(EnsureHandle());
        Render(typed: true);
        timer.Start();
    }

    // The once-a-second refresh while the box is open. Nothing typed means nothing is shown, so the catalogue
    // (one overview query, two COM calls per window) is not rebuilt for an empty box (finding 4).
    internal void Tick()
    {
        if (query.IsEmpty) return;
        refreshSources?.Invoke();
        all = catalog();
        Render(typed: false);
    }

    void Render(bool typed)
    {
        shown = FindRanking.Rank(query, all.Where(r => !gone.Contains(r.Key)).ToList(), recent());
        var groups = FindGrouping.Group(shown)
            .Select(g => new FindGroupView(g.Header, colourOf(g.Place), !g.Open,
                g.Results.Select(r => new FindRowView(r.Key, iconOf(r), r.Title, r.Detail)).ToList()))
            .ToList();
        window.Present(groups, query.Pieces, typed);
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
