using System.Text.Json.Serialization;

namespace Deskbar.Core.Tasks;

// THE TASKS PANEL'S RESIZABLE COLUMNS (task 3df2d28f, "use space more wisely"; Petre: "let's make it a grid, resizeable",
// "buttons shouldn't take so much space", "arrange them so that we're conserving space and putting as much text as we
// can"). A full line in the panel's List is a grid under a header row: the title takes what is left, and five text
// columns to its right have the widths kept here, which Petre sets by dragging the dividers in the header. The icon
// buttons (the flag toggles, comments, Open Claude, "⋯") are not here: they sit together at the line's right end, as
// narrow as their glyphs, and are not resizable.
//
// Before this every column had a fixed width sized for the widest thing it could ever hold ("2 sessions, 1 live", a
// long workspace name, Reopen), 560 DIP in all, so at a 740 DIP panel the title wrapped at about 140 DIP while the
// columns beside it were mostly air (Petre's screenshot on the task).
//
// Pure, so the window and the tests ask one place: the defaults, the narrowest a column may be dragged to, and how a
// divider's drag turns into a width.

/// <summary>Which resizable column of a full task line, in the order they stand, left to right after the title.</summary>
public enum TaskColumn { Status, Sessions, Assignee, Age, Workspace }

/// <summary>
/// The widths, in DIPs, of the five resizable columns of a full task line in the tasks panel, saved with the panel
/// (TasksPanelState.Columns). A record of numbers, so two equal sets compare equal and a drag that ends where it started
/// saves nothing (WorkspaceManager.SaveTasksPanel compares).
/// </summary>
public sealed record TaskColumnWidths(double Status, double Sessions, double Assignee, double Age, double Workspace)
{
    // THE DEFAULTS, measured against what each column usually holds, not the widest it ever could: "In progress" in its
    // chip (the status chip's 10 DIP text, 6 DIP padding each side and its 1 DIP edge); a square and "1 session";
    // "Claude"; an age up to "12h34m" after its 6 DIP margin; "Deskbar" in its chip. Wider content trims with an
    // ellipsis and is whole in the tooltip, and a drag widens the column for good.
    public static readonly TaskColumnWidths Default = new(Status: 72, Sessions: 70, Assignee: 42, Age: 40, Workspace: 72);

    /// <summary>The narrowest a column can be dragged to: room for a few letters and the ellipsis, so it never vanishes.</summary>
    public const double MinWidth = 24;

    /// <summary>The narrowest a drag may leave the title, so widening a column never squeezes the title out of the line.</summary>
    public const double MinTitle = 80;

    public double this[TaskColumn column] => column switch
    {
        TaskColumn.Status => Status,
        TaskColumn.Sessions => Sessions,
        TaskColumn.Assignee => Assignee,
        TaskColumn.Age => Age,
        _ => Workspace,
    };

    /// <summary>These widths with one column set to <paramref name="width"/>, as given (no clamping: see Dragged).</summary>
    public TaskColumnWidths With(TaskColumn column, double width) => column switch
    {
        TaskColumn.Status => this with { Status = width },
        TaskColumn.Sessions => this with { Sessions = width },
        TaskColumn.Assignee => this with { Assignee = width },
        TaskColumn.Age => this with { Age = width },
        _ => this with { Workspace = width },
    };

    /// <summary>The five widths in the order the columns stand.</summary>
    [JsonIgnore] public IReadOnlyList<double> InOrder => [Status, Sessions, Assignee, Age, Workspace];

    /// <summary>
    /// What a drag of the divider on <paramref name="column"/>'s LEFT edge makes of the widths. The line is anchored on
    /// its right (the icon buttons stay flush with the right edge), so a column grows to its left: dragging its left
    /// divider left by d widens it by d and the title, to its left, gives up d. Every other column keeps its width, so
    /// the divider follows the pointer. <paramref name="moved"/> is the pointer's move since the press, in DIPs, and
    /// <paramref name="startWidths"/> the widths then; <paramref name="titleAtStart"/> is the title's width then, which
    /// caps the growth at what keeps it MinTitle wide (a title already narrower than that cannot be squeezed further,
    /// but a column can still shrink). The column never goes under MinWidth.
    /// </summary>
    public static TaskColumnWidths Dragged(TaskColumnWidths startWidths, TaskColumn column, double moved, double titleAtStart)
    {
        var start = startWidths[column];
        var most = start + Math.Max(0, titleAtStart - MinTitle);
        return startWidths.With(column, Math.Round(Math.Clamp(start - moved, MinWidth, Math.Max(MinWidth, most))));
    }
}
