using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Automation;
using Deskbar.Core.Domain;

namespace Deskbar.App;

// HOW MANY TABS A BROWSER WINDOW HOLDS, and whether one of them is playing sound, for the bar's stacked-card
// mark (Petre: "let's include tab counts in browsers in their icons", then "c") and for telling which of a
// browser's windows the sound comes from.
//
// Read from the browser's own tab strip through UI Automation, measured on his machine first:
//
//   * 10 to 50 ms a window, on every desktop, by window handle (AutomationElement.FromHandle; the root's
//     children only list the current desktop's windows).
//   * Counting every TabItem is WRONG: YouTube Music's window has no tab strip and came back with 5, which
//     were tab controls inside the page ("UP NEXT", "LYRICS"...). So a TabItem counts only when no
//     Document lies between it and the window. With that, his Edge windows read 5, 1 and 2, and the one
//     whose title said "and 1 more page" read 2. An installed web app reads 0.
//
// VS CODE TOO (Petre: "can you also include vscode tab counts?"). Electron, so the same UI Automation, but its
// editor tabs live INSIDE its page, which the browser rule above skips by design, and the page has other
// TabItems as well. Measured on his windows:
//
//   Explorer (Ctrl+Shift+E)        class='action-item icon checked'   <- the activity bar's view switchers
//   Caption position redesign      class='tab tab-actions-right ... active selected'   <- an editor tab
//
// So for VS Code a TabItem counts when its class has the word "tab". And its tree is built lazily: the FIRST
// query of a window found no TabItems at all, the second (seconds later) found them, because Chromium only
// builds the web content's accessibility tree once something asks. A VS Code reading of 0 is therefore taken
// as "not ready" rather than as zero, and the window is simply read again on the next pass.
//
// Never on the UI thread: a browser busy enough to answer slowly must not stall the bar. One background
// worker reads windows as the bar asks for them (Watch), each at most every few seconds unless its title
// changed, and Changed fires only when a reading differs from the last one.
public sealed record TabInfo(int Count, bool PlayingAudio);

public interface IBrowserTabs
{
    // The last reading for a window, or null before the first one.
    TabInfo? Of(WindowHandle window);

    // The windows the bar is showing: browsers among them are read if stale, and windows no longer listed
    // are forgotten.
    void Watch(IReadOnlyList<WindowInfo> windows);

    // Fires when a COUNT or the audio mark changes, which is what the bar draws. A tab's title changing does
    // not fire it: page titles change all the time and the bar would rebuild for nothing.
    IObservable<Unit> Changed { get; }

    // QUICK FIND (docs/superpowers/specs/2026-11-04-quick-find-design.md): each tab's title, in tab strip
    // order, cleaned by TabTitle. Empty before the first reading. Read from the last background reading and
    // never on the caller's thread: a full pass over Petre's 25 windows took 4.9 s
    // (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
    IReadOnlyList<string> NamesOf(WindowHandle window);

    // Quick find opening: read these windows again if stale, like Watch, but forget nothing. Watch's list is
    // the bar's, and quick find must not make the bar lose readings for windows it is not showing.
    void Refresh(IReadOnlyList<WindowInfo> windows);

    // Quick find's Enter on a tab: make that tab the window's selected one. On a pool thread, never the
    // caller's, like every other UI Automation call here. `code` says the window is VS Code, whose editor tabs
    // are found by their class rather than by the browser rule.
    Task<CSharpFunctionalExtensions.Result> Select(WindowHandle window, bool code, int index, string title);
}

public sealed class BrowserTabs : IBrowserTabs, IDisposable
{
    // Process names of the browsers whose tab strips are read. An installed web app runs as one of these
    // too, and reads 0 tabs, which is how the bar tells it from a browser window.
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "brave", "vivaldi", "opera", "firefox" };

    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(4);

    readonly ConcurrentDictionary<WindowHandle, TabInfo> readings = new();
    readonly ConcurrentDictionary<WindowHandle, IReadOnlyList<string>> names = new();
    readonly ConcurrentDictionary<WindowHandle, (string Title, DateTime At)> lastRead = new();
    readonly ConcurrentDictionary<WindowHandle, byte> queued = new();
    readonly BlockingCollection<(WindowHandle Window, bool Code)> queue = new();
    readonly Subject<Unit> changed = new();
    readonly Thread worker;

    public BrowserTabs()
    {
        worker = new Thread(Work) { IsBackground = true, Name = "Deskbar tab counts" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public static bool IsBrowser(WindowInfo window) => Browsers.Contains(window.ProcessName);

    static bool IsCode(WindowInfo window) => window.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase);

    // Every app whose tabs are counted: the browsers, and VS Code.
    static bool HasTabs(WindowInfo window) => IsBrowser(window) || IsCode(window);

    public TabInfo? Of(WindowHandle window) => readings.TryGetValue(window, out var info) ? info : null;

    public IObservable<Unit> Changed => changed.AsObservable();

    public IReadOnlyList<string> NamesOf(WindowHandle window) => names.TryGetValue(window, out var found) ? found : [];

    public void Watch(IReadOnlyList<WindowInfo> windows)
    {
        var listed = windows.Select(w => w.Handle).ToHashSet();
        readings.Keys.Where(h => !listed.Contains(h)).ToList().ForEach(h =>
        {
            readings.TryRemove(h, out _);
            names.TryRemove(h, out _);
            lastRead.TryRemove(h, out _);
        });

        Refresh(windows);
    }

    public void Refresh(IReadOnlyList<WindowInfo> windows)
    {
        var now = DateTime.UtcNow;
        windows.Where(HasTabs)
            .Where(w => !lastRead.TryGetValue(w.Handle, out var last) || last.Title != w.Title || now - last.At > Fresh)
            .Where(w => queued.TryAdd(w.Handle, 0))
            .ToList()
            .ForEach(w =>
            {
                lastRead[w.Handle] = (w.Title, now);
                queue.Add((w.Handle, IsCode(w)));
            });
    }

    void Work()
    {
        foreach (var (window, code) in queue.GetConsumingEnumerable())
        {
            queued.TryRemove(window, out _);
            if ((code ? ReadCode(window) : Read(window)) is not var (reading, titles)) continue;
            // Titles are kept on every reading; only a change the bar draws fires Changed (see the interface).
            names[window] = titles;
            if (readings.TryGetValue(window, out var before) && before == reading) continue;
            readings[window] = reading;
            changed.OnNext(Unit.Default);
        }
    }

    // The browser's own tabs: TabItems with no Document between them and the window. A tab whose name says it
    // is playing audio marks the window as the sound's source. Chromium appends this to the tab's accessible
    // name ("... - Audio playing"); "muted" is the tab that would play but has been silenced.
    internal static (TabInfo Info, IReadOnlyList<string> Titles)? Read(WindowHandle window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Value);
            var walker = TreeWalker.ControlViewWalker;
            var tabNames = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .Where(tab => !InsidePage(tab, root, walker))
                .Select(tab => tab.Current.Name)
                .ToList();
            var playing = tabNames.Any(PlaysAudio);
            return (new TabInfo(tabNames.Count, playing), tabNames.Select(TabTitle).ToList());
        }
        catch
        {
            // A window that went away mid-read, or one that will not answer: no reading, and the last one stands.
            return null;
        }
    }

    // VS Code's editor tabs: TabItems whose class has the word "tab", in every editor group. None at all means
    // its accessibility tree has not been built yet (see the header), so there is no reading rather than a 0.
    internal static (TabInfo Info, IReadOnlyList<string> Titles)? ReadCode(WindowHandle window)
    {
        try
        {
            var tabNames = AutomationElement.FromHandle(window.Value)
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .Where(tab => IsEditorTab(tab.Current.ClassName))
                .Select(tab => tab.Current.Name)
                .ToList();
            return tabNames.Count > 0 ? (new TabInfo(tabNames.Count, false), tabNames.Select(TabTitle).ToList()) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsEditorTab(string className) =>
        className.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tab");

    // SELECTING A TAB from outside the browser. Measured on Petre's machine before this was written
    // (docs/superpowers/notes/2026-11-04-quick-find-probes.md): SelectionItemPattern.Select() switches Edge and
    // VS Code to the tab in 23 to 36 ms, with the window on another desktop and while minimised.
    //
    // The tab is re-found at the index it was listed under, and its title checked: tabs move and close between
    // the background reading and Enter. When the index no longer holds that title, the strip is searched for
    // it once, so a tab that moved is still found; when no tab has it, nothing is selected and the window
    // stays on whatever tab it shows.
    public Task<CSharpFunctionalExtensions.Result> Select(WindowHandle window, bool code, int index, string title) =>
        Task.Run(() => SelectNow(window, code, index, title));

    static CSharpFunctionalExtensions.Result SelectNow(WindowHandle window, bool code, int index, string title)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Value);
            var walker = TreeWalker.ControlViewWalker;
            var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .Where(tab => code ? IsEditorTab(tab.Current.ClassName) : !InsidePage(tab, root, walker))
                .ToList();
            var target = index < tabs.Count && SameTab(title, tabs[index].Current.Name)
                ? tabs[index]
                : tabs.FirstOrDefault(tab => SameTab(title, tab.Current.Name));
            if (target is null) return CSharpFunctionalExtensions.Result.Failure($"No tab titled \"{title}\" any more.");
            if (!target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                return CSharpFunctionalExtensions.Result.Failure($"The tab \"{title}\" cannot be selected from outside.");
            ((SelectionItemPattern)pattern).Select();
            return CSharpFunctionalExtensions.Result.Success();
        }
        catch (Exception e)
        {
            // A window that closed mid-way, or one that will not answer: the window is still where Enter took
            // Petre, only on its old tab.
            return CSharpFunctionalExtensions.Result.Failure($"Selecting \"{title}\" failed: {e.Message}");
        }
    }

    internal static bool SameTab(string listedTitle, string accessibleName) => TabTitle(accessibleName) == listedTitle;

    // A tab's title as Petre would type it: its accessible name without what the app appends. Measured on his
    // machine (docs/superpowers/notes/2026-11-04-quick-find-probes.md):
    //
    //   Edge     "Microsoft Partner - Sleeping", "New tab - Discarded", "... - Part of tab group Store",
    //            and "... - Audio playing" / "... - Audio muted" (the PlaysAudio marks below)
    //   VS Code  "Running apps quick find, Editor Group 1"
    //
    // Stripped repeatedly, because Edge can stack two ("... - Audio playing - Sleeping" would read as one
    // state after another). Only ever a whole trailing " - <state>" or ", Editor Group N", so a page titled
    // "Sleeping bags" is left alone.
    internal static string TabTitle(string accessibleName)
    {
        var title = accessibleName.Trim();
        for (var stripped = Strip(title); stripped != title; stripped = Strip(title)) title = stripped;
        return title;
    }

    static readonly System.Text.RegularExpressions.Regex AppendedState = new(
        @"(\s-\s(Audio playing|Audio muted|Sleeping|Discarded|Part of tab group\b.*)|,\sEditor Group \d+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static string Strip(string title) => AppendedState.Replace(title, "").TrimEnd();

    internal static bool PlaysAudio(string tabName) =>
        tabName.Contains("audio", StringComparison.OrdinalIgnoreCase)
        && tabName.Contains("playing", StringComparison.OrdinalIgnoreCase)
        && !tabName.Contains("muted", StringComparison.OrdinalIgnoreCase);

    static bool InsidePage(AutomationElement tab, AutomationElement window, TreeWalker walker)
    {
        for (var node = walker.GetParent(tab); node is not null && !Automation.Compare(node, window); node = walker.GetParent(node))
            if (node.Current.ControlType == ControlType.Document) return true;
        return false;
    }

    public void Dispose()
    {
        queue.CompleteAdding();
        changed.OnCompleted();
    }
}
