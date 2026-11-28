using CSharpFunctionalExtensions;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE "/" LIST UNDER A TEXT BOX (task 3a872301; Petre: "let me do a /skill in the workspace selected", and, pointing at
// the bar's add box, "why not within the textbox?"). While the box's text is "/" and the start of a name, with the caret
// in it, a list under the box shows the Claude Code skills that match (ClaudeSkills.Query and Match). The skills come
// from Claude Code (SkillCatalog, ClaudeSkillProbe), never from Deskbar reading folders itself: Petre, "so that we
// don't invent skills". Until the first answer for a folder arrives (about 3 s) the list says it is reading.
//
// Keys, read ahead of the box's own (the box's owner attaches this before its own PreviewKeyDown):
//   * Down and Up move the pick; Tab or Enter puts "/<skill> " in the box, caret after the space, for the arguments.
//   * Ctrl+Enter puts the pick in the box and lets the key go on, so the box's owner asks for the workspace (task
//     6184e8bf) and then adds the task and starts Claude on it ("/ref", Ctrl+Enter, Enter starts /refresh-pr).
//   * Esc closes the list and nothing else (the box keeps its text); it stays closed until the text changes.
// A click on a row picks it. The rows take no keyboard focus, so the caret stays in the box.
//
// Everything else is the box's: a space after the name, or any text that is not "/name", closes the list.
sealed class SkillCompletion
{
    /// <summary>The colours and size the list is drawn in, the box's own.</summary>
    public sealed record Look(Brush Fill, Brush Edge, Brush Ink, Brush DimInk, Brush PickedFill);

    /// <summary>A comment box's words (task 29bdd158): its list is the repo the task's session works in.</summary>
    public const string CommentAsking = "Asking Claude Code for this task's skills…";

    /// <summary>A comment box's words when the session's repo has no skills, or the task has no session.</summary>
    public const string CommentNone = "No skills here: this task's session repo has none in .claude/skills";

    // Rows shown before the list scrolls.
    const int VisibleRows = 10;

    readonly TextBox box;
    readonly Func<Task<IReadOnlyList<SkillChoice>>> source;
    readonly Func<bool> enabled;
    readonly Look look;
    readonly Popup popup;
    readonly StackPanel rows = new();
    readonly ScrollViewer scroller;

    // The list as Claude Code gave it for this opening; null while it is being read.
    IReadOnlyList<SkillChoice>? all;
    // Bumped at each opening, so an answer for an earlier one (another workspace's) is dropped.
    int opening;
    IReadOnlyList<SkillChoice> shown = [];
    int picked;
    // The text Esc closed the list on; the list stays shut until the text is something else.
    string? dismissed;

    // What the list says while Claude Code is asked, and when the folders have no skills of their own. The add box's words
    // name the workspace; a comment box's name the task (task 29bdd158), whose list is its session's repo.
    readonly string asking;
    readonly string none;

    public SkillCompletion(TextBox box, Func<Task<IReadOnlyList<SkillChoice>>> source, Func<bool> enabled, Look look,
        string asking = "Asking Claude Code for this workspace's skills…",
        string none = "No skills of this workspace's own repos (their .claude/skills)")
    {
        this.asking = asking;
        this.none = none;
        this.box = box;
        this.source = source;
        this.enabled = enabled;
        this.look = look;
        scroller = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            // WPF flips the list above the box when there is no room under it (a bar docked at the bottom).
            StaysOpen = true,
            AllowsTransparency = false,
            Focusable = false,
            Child = new Border
            {
                Background = look.Fill,
                BorderBrush = look.Edge,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 2, 0, 2),
                Child = scroller,
            },
        };
        box.PreviewKeyDown += OnKey;
        box.TextChanged += (_, _) => Refresh();
        // The caret moving (a click, Home, the arrows) can take it out of the name, which closes the list.
        box.SelectionChanged += (_, _) => Refresh();
        box.LostKeyboardFocus += (_, _) => Close();
        box.IsVisibleChanged += (_, _) =>
        {
            if (!box.IsVisible) Close();
        };
    }

    /// <summary>Whether the list is showing (for the tests).</summary>
    public bool IsOpen => popup.IsOpen;

    /// <summary>The skill names the list shows, in order (for the tests).</summary>
    public IReadOnlyList<string> Shown => shown.Select(s => s.Name).ToList();

    /// <summary>Closes the list as Esc does, keeping the text; whether it was open (the tasks panel's own Esc asks first).</summary>
    public bool Dismiss()
    {
        if (!popup.IsOpen) return false;
        dismissed = box.Text;
        Close();
        return true;
    }

    void Refresh()
    {
        if (dismissed is not null && box.Text != dismissed) dismissed = null;
        var query = ClaudeSkills.Query(box.Text, box.CaretIndex);
        if (query.HasNoValue || dismissed is not null || !enabled() || !box.IsKeyboardFocusWithin)
        {
            Close();
            return;
        }
        if (!popup.IsOpen) Open();
        Paint(query.Value);
    }

    void Open()
    {
        all = null;
        var mine = ++opening;
        popup.MinWidth = box.ActualWidth;
        popup.MaxWidth = Math.Max(box.ActualWidth, 260);
        popup.IsOpen = true;
        _ = Load(mine);
    }

    async Task Load(int mine)
    {
        IReadOnlyList<SkillChoice> answer;
        try
        {
            answer = await source();
        }
        catch (Exception e)
        {
            // The catalog answers failures as an empty list; anything that still throws is traced and shown as none.
            ClickTrace.Write($"skills: the list failed: {e.Message}");
            answer = [];
        }
        // The await came back on the box's dispatcher (it was started there). A later opening owns the list now.
        if (mine != opening || !popup.IsOpen) return;
        all = answer;
        ClaudeSkills.Query(box.Text, box.CaretIndex).Tap(Paint);
    }

    void Close()
    {
        if (!popup.IsOpen) return;
        popup.IsOpen = false;
        opening++;
        shown = [];
    }

    void Paint(string query)
    {
        rows.Children.Clear();
        if (all is null)
        {
            shown = [];
            rows.Children.Add(Note(asking));
            return;
        }
        var before = shown.ElementAtOrDefault(picked)?.Name;
        shown = ClaudeSkills.Match(all, query);
        // The pick stays on the same skill while it still matches, else goes to the best match.
        picked = Math.Max(0, shown.Select(s => s.Name).ToList().IndexOf(before ?? ""));
        if (shown.Count == 0)
        {
            rows.Children.Add(Note(all.Count == 0 ? none : $"No skill matches /{query}"));
            return;
        }
        shown.Select(Row).ToList().ForEach(row => rows.Children.Add(row));
        scroller.MaxHeight = VisibleRows * (box.FontSize * 1.5 + 2);
        ShowPicked();
    }

    TextBlock Note(string text) =>
        new()
        {
            Text = text,
            Foreground = look.DimInk,
            FontSize = box.FontSize,
            Margin = new Thickness(6, 2, 6, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    // A row: "/name", then, for a project's skill, the repos that have it, dimmed.
    Border Row(SkillChoice skill, int index)
    {
        var text = new TextBlock { FontSize = box.FontSize, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run("/" + skill.Name) { Foreground = look.Ink });
        if (skill.Folders.Count > 0)
            text.Inlines.Add(new System.Windows.Documents.Run("  " + string.Join(", ", skill.Folders.Select(Leaf)))
                { Foreground = look.DimInk });
        var row = new Border
        {
            Child = text,
            Padding = new Thickness(6, 1, 6, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = skill.Name,
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            picked = index;
            Pick();
        };
        return row;
    }

    static string Leaf(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : folder;

    void ShowPicked()
    {
        rows.Children.OfType<Border>().Select((row, i) => (row, i)).ToList()
            .ForEach(x => x.row.Background = x.i == picked ? look.PickedFill : Brushes.Transparent);
        if (rows.Children.OfType<Border>().ElementAtOrDefault(picked) is { } current) current.BringIntoView();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!popup.IsOpen) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Down when shown.Count > 0:
                picked = Math.Min(picked + 1, shown.Count - 1);
                ShowPicked();
                e.Handled = true;
                break;
            case Key.Up when shown.Count > 0:
                picked = Math.Max(picked - 1, 0);
                ShowPicked();
                e.Handled = true;
                break;
            case Key.Tab when shown.Count > 0:
            case Key.Enter when shown.Count > 0 && !ctrl && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                Pick();
                e.Handled = true;
                break;
            // Ctrl+Enter: the pick goes in, and the key goes on to the box's owner, which adds and starts Claude.
            case Key.Enter when shown.Count > 0 && ctrl:
                Pick();
                break;
            case Key.Escape:
                dismissed = box.Text;
                Close();
                e.Handled = true;
                break;
        }
    }

    void Pick()
    {
        if (shown.ElementAtOrDefault(picked) is not { } skill) return;
        var (text, caret) = ClaudeSkills.Complete(box.Text, box.CaretIndex, skill.Name);
        Close();
        box.Text = text;
        box.CaretIndex = caret;
    }
}
