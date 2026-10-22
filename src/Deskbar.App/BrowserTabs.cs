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

    IObservable<Unit> Changed { get; }
}

public sealed class BrowserTabs : IBrowserTabs, IDisposable
{
    // Process names of the browsers whose tab strips are read. An installed web app runs as one of these
    // too, and reads 0 tabs, which is how the bar tells it from a browser window.
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "brave", "vivaldi", "opera", "firefox" };

    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(4);

    readonly ConcurrentDictionary<WindowHandle, TabInfo> readings = new();
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

    public void Watch(IReadOnlyList<WindowInfo> windows)
    {
        var listed = windows.Select(w => w.Handle).ToHashSet();
        readings.Keys.Where(h => !listed.Contains(h)).ToList().ForEach(h =>
        {
            readings.TryRemove(h, out _);
            lastRead.TryRemove(h, out _);
        });

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
            var reading = code ? ReadCode(window) : Read(window);
            if (reading is null) continue;
            if (readings.TryGetValue(window, out var before) && before == reading) continue;
            readings[window] = reading;
            changed.OnNext(Unit.Default);
        }
    }

    // The browser's own tabs: TabItems with no Document between them and the window. A tab whose name says it
    // is playing audio marks the window as the sound's source. Chromium appends this to the tab's accessible
    // name ("... - Audio playing"); "muted" is the tab that would play but has been silenced.
    internal static TabInfo? Read(WindowHandle window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Value);
            var walker = TreeWalker.ControlViewWalker;
            var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .Where(tab => !InsidePage(tab, root, walker))
                .ToList();
            var playing = tabs.Any(tab => PlaysAudio(tab.Current.Name));
            return new TabInfo(tabs.Count, playing);
        }
        catch
        {
            // A window that went away mid-read, or one that will not answer: no reading, and the last one stands.
            return null;
        }
    }

    // VS Code's editor tabs: TabItems whose class has the word "tab", in every editor group. None at all means
    // its accessibility tree has not been built yet (see the header), so there is no reading rather than a 0.
    internal static TabInfo? ReadCode(WindowHandle window)
    {
        try
        {
            var tabs = AutomationElement.FromHandle(window.Value)
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .Count(tab => IsEditorTab(tab.Current.ClassName));
            return tabs > 0 ? new TabInfo(tabs, false) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsEditorTab(string className) =>
        className.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tab");

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
