using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// A RUN OF STEPS, FOLDED (the picked mockup: r2-app.js stepsHTML and stepHTML, r2.css .steps). Between two of Claude's messages
// the steps it took are one block: a header that says "3 steps · 2 min" with a short summary at its right ("1 file changed ·
// 2 commands"), and under it, once the header is pressed, one row per step: an icon for its kind, "Read Dock.cs", and where
// it matters a word.
//
// A STATE IS A SHAPE AND A WORD, NEVER COLOUR ALONE (the owner is colourblind; the Inbox's rule, InboxMarks):
//   * a failed step wears the cross-in-a-circle icon and the word "failed", and the header counts them ("1 failed");
//   * a step still running wears the turning arc and the word "running", and the header says "running" with the last such
//     step's words, so a folded block shows what Claude is doing now;
//   * a finished step wears a small tick and no word (a block of fifty "done" would be noise).
//
// IT IS UPDATED IN PLACE (Update): a poll that brings a new step or a finished one changes this block's header and rows and
// nothing else, so the block's folded or open state, its place in the tree and the keyboard on its header all stay. The open
// state itself is kept outside it, in ChatStreamState keyed by the run's Id, so a page that is rebuilt (a new comment
// redraws the whole page) opens the block again as it was.
internal sealed class ChatStepsView : Border
{
    /// <summary>The Tag of a run's block, for the tests.</summary>
    internal const string StepsTag = "chat-steps";

    /// <summary>The Tag of one step's row, for the tests.</summary>
    internal const string RowTag = "chat-step";

    // The mockup's .steps background (#0C0F14), a shade under the Inbox's own: the block reads as a recess in the chat.
    static readonly Brush Recess = Frozen(0x0C, 0x0F, 0x14);

    // A folded run's line: the size of the chat's grey times and captions, a step under the chat's text (InboxLook.ChatText).
    const double FoldedText = 12;

    readonly ChatStreamDeps deps;
    readonly Button header;
    readonly StackPanel rows = new();
    readonly Dictionary<string, Row> rowOf = new(StringComparer.Ordinal);
    string runId;

    // Whether the session is live and this is its newest run: only then can a step without a result be running (Update).
    bool live;

    public ChatStepsView(ChatSteps run, ChatStreamDeps deps, bool live = true)
    {
        this.live = live;
        this.deps = deps;
        runId = run.Id;
        Tag = StepsTag;
        // The recess, its hairline and the gap under it are set by Paint: a folded run has none of them.
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        // The whole column's width, like the mockup's align-self: stretch.
        HorizontalAlignment = HorizontalAlignment.Stretch;
        header = deps.Button(new Border(), "steps", Toggle);
        header.Focusable = true;
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var column = new StackPanel();
        column.Children.Add(header);
        column.Children.Add(rows);
        Child = column;
        Update(run, live);
    }

    bool Open => deps.State.OpenRuns.Contains(runId);

    void Toggle()
    {
        if (!deps.State.OpenRuns.Remove(runId)) deps.State.OpenRuns.Add(runId);
        Paint();
    }

    ChatSteps current = null!;

    /// <summary>Takes the run as it is now: its header, and its rows for new or changed steps. Rows of unchanged steps are not touched.</summary>
    /// <param name="run">The run as it is now.</param>
    /// <param name="live">
    /// Whether the session runs AND this is its newest run (tester finding M3). A step with no result is "running" only then;
    /// in an older run, or once the session ended, it has no result and never will, so it says "no result", with a shape of its
    /// own, and its mark does not turn.
    /// </param>
    public void Update(ChatSteps run, bool live = true)
    {
        current = run;
        this.live = live;
        foreach (var step in run.Steps)
        {
            if (rowOf.TryGetValue(step.Id, out var row))
            {
                if (row.Step != step || row.Running != live) row.Set(step, live);
                continue;
            }
            var added = new Row(step, live);
            rowOf[step.Id] = added;
            rows.Children.Add(added.Frame);
        }
        Paint();
    }

    // The header (title, summary, chevron) and whether the rows show.
    void Paint()
    {
        var open = Open;
        rows.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // FOLDED, A RUN OF STEPS IS ONE DIM LINE (task ac1ebd57, Petre: "make these more compact"; mockup option A): no
        // recess, no hairline, the small grey text, "› 3 steps · 9 s · 3 commands" in one line, so it sits between two
        // bubbles like a caption instead of a card. A run of steps also no longer ends an author's run of messages
        // (ChatStreamPanel.Unnamed), so the name is not written above and below it. OPENED, it is the recessed block with
        // its rows that it always was: the steps are then what is being read.
        Background = open ? Recess : null;
        BorderBrush = open ? InboxLook.Hairline : null;
        Margin = new Thickness(0, 0, 0, open ? 4 : 2);
        var size = open ? InboxLook.ChatText : FoldedText;
        var steps = current.Steps;
        var title = steps.Count == 1 ? "1 step" : $"{steps.Count} steps";
        var duration = DurationOf(steps);
        AutomationProperties.SetName(header, title);
        AutomationProperties.SetHelpText(header, open ? "Press to fold the steps" : "Press to show the steps");
        // Two text blocks side by side, not one with two runs: a text block's Text is empty while it holds inlines, and
        // the words are what a test and a screen reader read.
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = title, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = open ? InboxLook.Ink : InboxLook.Ink2 });
        if (duration.Length > 0) heading.Children.Add(new TextBlock { Text = " · " + duration, FontSize = size, Foreground = open ? InboxLook.Ink2 : InboxLook.Ink3 });
        var summary = new TextBlock
        {
            Text = open ? SummaryOf(steps, live) : " · " + SummaryOf(steps, live), FontSize = open ? 13 : FoldedText, Foreground = InboxLook.Ink3,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = open ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(open ? 10 : 0, 0, 0, 0),
        };
        var chevron = InboxMarks.Icon(open ? "chevD" : "chevR", open ? 12 : 10, InboxLook.Ink2);
        chevron.Margin = new Thickness(0, 0, open ? 8 : 5, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        // Folded, the summary follows the heading on the same line; opened, it sits at the block's right edge.
        var line = new DockPanel { LastChildFill = true, Margin = open ? new Thickness(11, 7, 11, 7) : new Thickness(4, 1, 4, 1) };
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(summary, open ? Dock.Right : Dock.Left);
        DockPanel.SetDock(heading, Dock.Left);
        line.Children.Add(chevron);
        if (open) line.Children.Add(summary);
        line.Children.Add(heading);
        if (!open) line.Children.Add(summary);
        ((Border)header.Content).Child = line;
    }

    // "2 min", "40 s" or "" for one step with no time; from the first step's moment to the last one's.
    static string DurationOf(IReadOnlyList<ChatStep> steps)
    {
        var times = steps.Where(s => s.At.HasValue).Select(s => s.At.Value).ToList();
        if (times.Count < 2) return "";
        var span = times[^1] - times[0];
        return span < TimeSpan.FromMinutes(1) ? $"{Math.Max(1, (int)span.TotalSeconds)} s" : $"{(int)Math.Round(span.TotalMinutes)} min";
    }

    // "running Ran dotnet test · 1 failed · 2 files changed · 3 commands": what is happening, what went wrong, what was done.
    // Words, so a folded block still tells a failure apart without opening it.
    static string SummaryOf(IReadOnlyList<ChatStep> steps, bool live)
    {
        var parts = new List<string>();
        if (live && steps.LastOrDefault(s => !s.Done) is { } running) parts.Add("running " + ChatStepIcons.TextOf(running));
        var failed = steps.Count(s => s.Failed);
        if (failed > 0) parts.Add($"{failed} failed");
        var unanswered = live ? 0 : steps.Count(s => !s.Done);
        if (unanswered > 0) parts.Add(unanswered == 1 ? "1 with no result" : $"{unanswered} with no result");
        var files = steps.Where(s => s.Kind is StepKind.Edit or StepKind.Write).Select(s => s.Label).Distinct().Count();
        if (files > 0) parts.Add(files == 1 ? "1 file changed" : $"{files} files changed");
        var commands = steps.Count(s => s.Kind == StepKind.Run);
        if (commands > 0) parts.Add(commands == 1 ? "1 command" : $"{commands} commands");
        return string.Join(" · ", parts);
    }

    // One step's row. Set rewrites it in place when its state changes (a result arrives for a step that was running).
    sealed class Row
    {
        public ChatStep Step { get; private set; }
        /// <summary>Whether a step with no result was drawn as running (its run is the newest of a live session).</summary>
        public bool Running { get; private set; }
        public Grid Element { get; } = new();
        readonly TextBlock text = new() { FontSize = InboxLook.ChatText, Foreground = InboxLook.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock word = new() { FontSize = 13, Foreground = InboxLook.Ink2, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        readonly Border kind = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        readonly Border state = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

        public Row(ChatStep step, bool live)
        {
            Step = step;
            // Icon, words (the rest of the width), state word, state mark.
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                Element.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            kind.Margin = new Thickness(0, 0, 9, 0);
            Place(kind, 0);
            Place(text, 1);
            Place(word, 2);
            Place(state, 3);
            var rule = new Border { Tag = RowTag, BorderBrush = InboxLook.Hairline, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(11, 5, 11, 5), Child = Element };
            Frame = rule;
            Set(step, live);
        }

        public Border Frame { get; }

        void Place(FrameworkElement part, int column)
        {
            Grid.SetColumn(part, column);
            Element.Children.Add(part);
        }

        public void Set(ChatStep step, bool live)
        {
            Step = step;
            Running = live;
            var words = ChatStepIcons.TextOf(step);
            text.Text = words;
            text.ToolTip = words;
            kind.Child = InboxMarks.Icon(ChatStepIcons.IconOf(step.Kind), 14, InboxLook.Ink2);
            if (step.Failed)
            {
                word.Text = "failed";
                word.Visibility = Visibility.Visible;
                state.Child = InboxMarks.Icon("fail", 14, InboxLook.Ink);
            }
            else if (!step.Done && live)
            {
                word.Text = "running";
                word.Visibility = Visibility.Visible;
                state.Child = InboxMarks.Icon("working", 14, InboxLook.Blue, turning: true);
            }
            else if (!step.Done)
            {
                // No result and no one left to write it: neither running nor failed. A ring with a dash, and the words.
                word.Text = "no result";
                word.Visibility = Visibility.Visible;
                state.Child = InboxMarks.Icon("blank", 14, InboxLook.Ink3);
            }
            else
            {
                word.Text = "";
                word.Visibility = Visibility.Collapsed;
                state.Child = InboxMarks.Icon("tick", 14, InboxLook.Ink3);
            }
            AutomationProperties.SetName(Frame, step.Failed ? words + ", failed" : !step.Done ? words + (live ? ", running" : ", no result") : words);
        }
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
