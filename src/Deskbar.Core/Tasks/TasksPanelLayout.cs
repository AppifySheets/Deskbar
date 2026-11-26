namespace Deskbar.Core.Tasks;

// The tasks panel's width rules, kept pure so the dock, the drag handle and the task lines all ask one place and a
// test pins every threshold. Widths are DIPs. Spec: docs/superpowers/specs/2026-11-23-tasks-panel-design.md.
public static class TasksPanelLayout
{
    // Where a panel that was never resized starts, and the narrowest it may be dragged to.
    public const double DefaultWidth = 460;
    public const double MinWidth = 360;

    // Below this a task line drops to its slim form (the secondary details leave); from it, the full line.
    //
    // WHERE THE FULL LINE STARTS IS SET BY ITS TITLE'S ROOM, NOT BY WHAT MERELY FITS (task 39d58e5b, Petre: "wider bar
    // renders worse than narrow", with two screenshots). It was 710, the narrowest width at which the full line's columns
    // still fitted beside a 100 DIP title: 620 for the old floating window, 692 after the status chip column, 710 after
    // the snooze moon. At 710 the full line's fixed parts left the title 100 DIP, so a title wrapped every two or three
    // words into five lines, while one DIP narrower the slim line gave the same title about 600 DIP: one line, with its
    // controls on a second row under it. Widening the panel made every line taller and harder to read.
    //
    // Two things changed, Petre's pick of option B on the task:
    //   - The full line's title never goes under FullTitleReadable, about 50 characters a line, so a typical title of 60
    //     to 100 characters takes two lines. The strip of menu buttons at the line's end gives up its buttons first, down
    //     to "⋯" alone (TasksWindow.Columns.cs, FullTitleFloor).
    //   - The full line starts where that title and "⋯" fit beside the fixed columns of a usual list. Those are about 430
    //     DIP with a list of notes (insets and grip 35, tick 22, the five text columns 296, toggles 54, comments 22) and
    //     about 490 with a Claude task's Open Claude, two-digit comment counts and the scroll bar. 490 + 332 for the
    //     title's column + 22 for "⋯" = 844, so 850. A window test measures the title and "⋯" at this width. Where a line
    //     has less (a closed task's Reopen on show, a subtask's line inside its parent's thread, text columns dragged
    //     wide), the title's floor gives way a little so "⋯" stays on the line (TasksWindow.FitTitleFloor).
    //
    // 850 is also under what the panel can be on a 2560 pixel screen at 150% (half the screen, 853 DIP), Petre's own, so
    // the column header and resizable columns stay reachable there. Option A, a 940 DIP threshold with the strip keeping
    // its buttons, was rejected because it made them unreachable on that screen.
    public const double FullTitleReadable = 320;
    public const double SlimBelow = 850;

    // The List / Matrix switch is shown only from this width: the 2 by 2 needs room to be readable.
    public const double MatrixFrom = 780;

    // Keeps a width between the minimum and half the screen, so the panel never swallows the work area. A screen
    // narrower than twice the minimum still gets the minimum: the lower bound wins, not the half.
    public static double Clamp(double width, double screenWidthDips) =>
        Math.Clamp(width, MinWidth, Math.Max(MinWidth, screenWidthDips / 2));

    public static bool Slim(double width) => width < SlimBelow;

    public static bool MatrixAllowed(double width) => width >= MatrixFrom;
}
