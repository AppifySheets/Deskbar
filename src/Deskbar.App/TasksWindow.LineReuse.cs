using System.Windows;
using System.Windows.Controls;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// LINES KEPT BETWEEN DRAWS (task d0148d3b, Petre: "clicking and opening tasks in deskbar is slow, make it fast and
// snappy"). Every change the panel draws used to throw away every line and build them all again: a click on one line,
// which only opens that line's thread, rebuilt all fifty of them. Measured over a copy of Petre's tasks.json (306
// tasks, 51 lines on show, a probe on the hidden test desktop, 2026-11-27): 200 to 370 ms from the click to the next
// frame, of which the view and its signature took under 10 ms, building the lines about 150 ms and WPF's layout of the
// new elements about 180 ms. Both of the big two grow with the number of elements created, not with what changed.
//
// So the List keeps each line as it was drawn, with its thread when that is open (a "block"), under a key spelling out
// everything the block draws, and a draw builds only the blocks whose key changed. The kept ones stay where they are
// in the panel, so WPF does not measure them again either: a click costs one line and its thread.
//
// WHAT THE KEY HOLDS, and why that is enough. A block is drawn from its row (TaskRowView, every member of it, with its
// comments, sessions, links, pull requests and the rows of subtasks open in its thread spelled out, RowKey) and from
// state of the window that is the same for every line (SharedLineKey): the column widths and slim flag the line is
// laid out with, the folds a thread can open (settled and earlier comments, long comments, dragged heights), the
// workspaces, the panel's workspace filter, and today's date, since a session's time reads "HH:mm" today and "d MMM
// HH:mm" on any other day. The selection is not in it: PaintSelection paints the kept frames in place, as before. Nor
// the age: each List draw that kept a line rewrites every age for the time now (WriteAges), and RefreshAges once a
// minute, as before. A block that drew a picture it could not read is never kept (LineBlock.Keepable). Nor a comment box's draft: a kept box simply
// keeps its text, and its caret and keyboard with it.
//
// WHAT A BLOCK WROTE. Drawing a line also fills the window's registries (the menu buttons, selection frames, ages,
// comment boxes, drag lines and the rest, every one keyed by task id). They are emptied at each draw as before, so a
// block records what its drawing wrote into each, and a kept block writes those entries back (Record, Replay). The
// draw then leaves the registries exactly as a full rebuild would have.
//
// WHEN NOTHING IS KEPT. While a title is renamed, a comment edited, a link picked or a subtask added in a thread, the
// window holds fields pointing into one block's elements (renameBox, commentEditor, linkBox, subtaskBox) that only a
// fresh draw sets again, so every block is built afresh then, as before this change. The Matrix and the Stats view are
// drawn afresh as before; leaving the List drops every kept block.
public partial class TasksWindow
{
    // A line as drawn: its key, its elements in order (the line, then its thread when open) and the registry writes
    // its drawing made, to be made again when it is kept.
    // `Keepable` is false for a block that drew a picture it could not read (TaskImageRow's stand-in text): TaskImageRow
    // promises that the next redraw tries the file again, so that block is built afresh every draw until it reads.
    sealed record LineBlock(string Key, IReadOnlyList<UIElement> Elements, IReadOnlyList<Action> Writes, bool Keepable);

    // The blocks of the last List draw, by task id.
    Dictionary<Guid, LineBlock> keptBlocks = [];

    // For the tests and the trace: how many blocks the last List draw built, and how many it kept.
    internal int LinesBuilt { get; private set; }

    internal int LinesKept { get; private set; }

    // The List: each group's header, then each row's block, kept when its key is unchanged, else built; then the panel's
    // children brought to that order with as few moves as it takes (ShowRows).
    void DrawList(TasksView view)
    {
        var reusable = renaming is null && editingComment is null && linking is null && subtaskUnder is null;
        var shared = SharedLineKey(view);
        var drawnNow = new Dictionary<Guid, LineBlock>();
        var wanted = new List<UIElement>();
        LinesBuilt = 0;
        LinesKept = 0;
        view.Groups.Select((group, i) => (Group: group, Above: i > 0 ? view.Groups[i - 1].Kind : (TaskGroupKind?)null)).ToList().ForEach(x =>
        {
            // Headers are few and cheap, so they are built every time.
            wanted.Add(Header(x.Group, x.Above));
            x.Group.Rows.ToList().ForEach(row =>
            {
                closing[row.Id] = row.Closing;
                // The Claude working block's idle lines are dimmed (task 10b49554, see the header). Only Claude's: a
                // note nested under an idle parent is no idle Claude work. Part of the key, since the line wears it.
                var idle = x.Group.Kind == TaskGroupKind.ClaudeIdle && row.Claudes;
                if (idle) idleLines.Add(row.Id);
                var key = $"{shared}\u0005{idle}\u0005{RowKey(row)}";
                // A task drawn twice in one list (never expected) gets a second block of its own: one element cannot sit
                // in the panel twice.
                var block = reusable && !drawnNow.ContainsKey(row.Id) && keptBlocks.TryGetValue(row.Id, out var kept) && kept.Keepable && kept.Key == key
                    ? Replay(kept)
                    : Record(key, () => BuildBlock(row, idle));
                drawnNow.TryAdd(row.Id, block);
                wanted.AddRange(block.Elements);
            });
        });
        keptBlocks = reusable ? drawnNow : [];
        ShowRows(wanted);
        // A kept line's age is as old as its drawing: brought up to now, as a full rebuild would have drawn it.
        if (LinesKept > 0) WriteAges(Now());
    }

    // A row's line, dimmed when idle, and its thread under it when open: what the List drew for a row before blocks.
    IReadOnlyList<UIElement> BuildBlock(TaskRowView row, bool idle)
    {
        LinesBuilt++;
        var line = Line(row);
        if (idle) line.Opacity = IdleOpacity;
        // The thread capped under its line, so the two always fit in the view together (task ee9bd3fc).
        return row.Expanded ? [line, Thread(row, under: line)] : [line];
    }

    LineBlock Replay(LineBlock kept)
    {
        LinesKept++;
        kept.Writes.ToList().ForEach(write => write());
        return kept;
    }

    // Draws a block and notes what the drawing wrote into each registry, to be written again when the block is kept.
    LineBlock Record(string key, Func<IReadOnlyList<UIElement>> draw)
    {
        Func<Action>[] marks =
        [
            Since(menuButtons), Since(lineButtons), Since(dragLines), Since(dragEntries), Since(commentBoxes), Since(ages),
            Since(slimTips), Since(selectionFrames), Since(authors), Since(shownComments), Since(closing), Since(pendingRows),
            // A kept thread's scroller, frame and line (task ee9bd3fc, TasksWindow.ThreadCap.cs), so its scroll is read
            // at the next draw and ScrollToSelected can show it with its line.
            Since(cappedThreads),
        ];
        var gridsBefore = fullGrids.Count;
        var elements = draw();
        var grids = fullGrids.Skip(gridsBefore).ToArray();
        return new LineBlock(key, elements, marks.Select(mark => mark()).Append(() => fullGrids.AddRange(grids)).ToArray(),
            Keepable: !elements.SelectMany(Within).Any(node => node is FrameworkElement { Tag: TaskImageRow.MissingTag }));
    }

    // An element and everything in its logical tree.
    static IEnumerable<object> Within(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Within) : []);

    // A registry as it stands, and then, once the block is drawn, the entries the drawing added or replaced, as one write.
    static Func<Action> Since<T>(Dictionary<Guid, T> registry)
    {
        var before = new Dictionary<Guid, T>(registry);
        return () =>
        {
            var written = registry.Where(p => !before.TryGetValue(p.Key, out var was) || !EqualityComparer<T>.Default.Equals(was, p.Value)).ToArray();
            return () => written.ToList().ForEach(p => registry[p.Key] = p.Value);
        };
    }

    // The panel's children brought to `wanted`, in order. Children no longer wanted go; a kept one stays in place, so WPF
    // keeps its layout; a new one is inserted where it belongs. A kept block that changed place (a task moved between
    // groups) is moved, which re-measures only it.
    void ShowRows(IReadOnlyList<UIElement> wanted)
    {
        var keep = wanted.ToHashSet();
        Rows.Children.Cast<UIElement>().Where(child => !keep.Contains(child)).ToList().ForEach(Rows.Children.Remove);
        wanted.Select((element, index) => (element, index)).ToList().ForEach(x =>
        {
            if (x.index < Rows.Children.Count && ReferenceEquals(Rows.Children[x.index], x.element)) return;
            Rows.Children.Remove(x.element);
            Rows.Children.Insert(x.index, x.element);
        });
    }

    // Empties the List and lets go of every kept block: the Matrix and the Stats view draw elsewhere.
    void LeaveList()
    {
        Rows.Children.Clear();
        keptBlocks = [];
        LinesBuilt = 0;
        LinesKept = 0;
    }

    // What every line is drawn with besides its own row (see the header).
    string SharedLineKey(TasksView view) =>
        string.Join('\u0001',
            $"{view.Where}{view.ShowsSubtasks}{view.Slim}{slimLines}{slimSquaresWidth}",
            $"{commentsWidth}{actionWidth}{reevaluateLeads}{columnWidths}",
            string.Join('|', settledOpen.Order()), string.Join('|', earlierOpen.Order()), string.Join('|', longOpen.Order()),
            BodyHeightsSignature,
            string.Join('|', view.Places.Select(p => $"{p.IsGroup}{p.Workspace}{p.Name}{(p.Colour as System.Windows.Media.SolidColorBrush)?.Color}")),
            $"{Now().ToLocalTime().Date:yyyy-MM-dd}");

    // Everything a row draws, as text: the record's own members (a record prints them all), with each list spelled out,
    // and the row of each subtask open in its thread the same way, nested.
    internal static string RowKey(TaskRowView row) =>
        string.Join('\u0006',
            (row with { Comments = [], SessionList = [], Subtasks = [], Links = [], PullRequests = [] }).ToString(),
            string.Join('\u0007', row.Comments),
            string.Join('\u0007', row.SessionList),
            string.Join('\u0007', row.Links),
            string.Join('\u0007', row.PullRequests),
            string.Join('\u0007', row.Subtasks.Select(sub => (sub with { Row = null }) + (sub.Row is { } open ? "{" + RowKey(open) + "}" : ""))));
}
