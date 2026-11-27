using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Deskbar.App;

// ICONS ON A TASK'S MENUS (task e3f94ead "give icons"; Petre, on a screenshot of a line's "⋯" menu, picked option A of the
// mockup at https://claude.ai/artifact/WcMuowWYcoLA4vKCEb9SNw). The three menus that list a task's actions (the tasks
// panel's line menu, TasksWindow.OpenMenuFor; the bar's line menu, FloatingBar.OpenTaskLineMenu; the task window's "⋯",
// TaskDetailWindow.OpenMenu) were words only. Every top-level item now has an icon from the icon font the task list
// already draws with (FloatingBar.IconFont, Segoe Fluent Icons), in the menu's own ink.
//
// ONE PICTURE PER ACTION. An action that is also an icon button on the line (the strip of TasksWindow.LineButtons.cs: Edit,
// Open in window, Add subtask, Assign to, Related to, Move to, Delete; and the line's moon, repeat mark, Reevaluate and
// Claude spark) wears the same picture in the menu, so the strip reads the code points from here too and the two cannot
// drift apart. The rest are new, and picked in the mockup: Copy (Copy link), a checklist (Status), a tick (Close), an
// undo arrow (Reopen), stacked layers (Duplicate of), an up arrow (Open parent), an arrow up to a bar (Mark deployed) and
// a flag (Goal).
//
// LEFT WITHOUT AN ICON on purpose: the items inside a submenu (the statuses, snooze lengths, repeat schedules, Me and
// Claude, "Delete for good"), which carry ticks where one is picked, and the bar's checkable Important and Urgent, whose
// tick sits in the same column an icon would take.
//
// A code point that turns out wrong renders as a hollow box, not as an error (FloatingBar.Shell.cs says the same of the
// shell's icons); every one below was drawn and looked at before it went in.
internal static class TaskMenuIcons
{
    internal const string Edit = "";           // pencil (the line's Edit)
    internal const string OpenInWindow = "";   // a window (the line's Open in window)
    internal const string AddSubtask = "";     // plus (the line's Add subtask)
    internal const string AssignTo = "";       // a person (the line's Assign to)
    internal const string RelatedTo = "";      // a chain link (the line's Related to)
    internal const string MoveTo = "";         // a folder with an arrow (the line's Move to)
    internal const string Delete = "";         // a bin (the line's Delete)
    internal const string Reevaluate = "";     // refresh (the line's Reevaluate)
    internal const string Snooze = "";         // the moon (the line's snooze button)
    internal const string Repeat = "";         // repeat arrows (the line's repeat mark)
    internal const string CopyLink = "";       // Windows' own Copy
    internal const string Status = "";         // a checklist
    internal const string Close = "";          // a tick
    internal const string Reopen = "";         // an undo arrow
    internal const string DuplicateOf = "";    // stacked layers: the same task twice
    internal const string OpenParent = "";     // an up arrow: one level up the family
    internal const string MarkDeployed = "";   // an arrow up to a bar
    internal const string Goal = "";           // a flag
    internal const string GoToWorkspace = "";  // an arrow leaving a box: go where the task lives (task window only)
    internal const string ShowInPanel = "";    // a bulleted list: the tasks panel (task window only)

    /// <summary>A menu item's icon: <paramref name="glyph"/> in the icon font, inheriting the item's ink.</summary>
    internal static UIElement Glyph(string glyph) => new TextBlock
    {
        Text = glyph,
        FontFamily = FloatingBar.IconFont,
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The Claude spark the line's Claude button wears (TasksWindow.ClaudeSpark): orange on Open Claude (the task has a
    /// session), the menu's own ink on Start Claude on this, as on the line. A Path has no Foreground of its own, so the
    /// plain one binds its stroke to the inherited TextElement.Foreground, the very ink the glyph icons beside it get
    /// from the item's template (hover and disabled included).
    /// </summary>
    internal static UIElement Claude(bool hasSession)
    {
        var spark = new System.Windows.Shapes.Path
        {
            Tag = TasksWindow.ClaudeMarkTag,
            Data = TasksWindow.ClaudeSpark,
            Width = 12,
            Height = 12,
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (hasSession) spark.Stroke = TasksWindow.ClaudeSessionInk;
        else spark.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        return spark;
    }

    /// <summary>Gives <paramref name="item"/> the icon <paramref name="glyph"/> and hands it back, for use inline.</summary>
    internal static MenuItem WithIcon(this MenuItem item, string glyph)
    {
        item.Icon = Glyph(glyph);
        return item;
    }

    /// <summary>Gives <paramref name="item"/> the Claude spark (Claude) and hands it back.</summary>
    internal static MenuItem WithClaude(this MenuItem item, bool hasSession)
    {
        item.Icon = Claude(hasSession);
        return item;
    }
}
