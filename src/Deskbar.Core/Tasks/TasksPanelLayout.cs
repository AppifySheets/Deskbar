namespace Deskbar.Core.Tasks;

// The tasks panel's width rules, kept pure so the dock, the drag handle and the task lines all ask one place and a
// test pins every threshold. Widths are DIPs. Spec: docs/superpowers/specs/2026-11-23-tasks-panel-design.md.
public static class TasksPanelLayout
{
    // Where a panel that was never resized starts, and the narrowest it may be dragged to.
    public const double DefaultWidth = 460;
    public const double MinWidth = 360;

    // Below this a task line drops to its slim form (the secondary details leave); from it, the full line. 692 is the
    // full line's own floor: 620 was the old floating window's minimum width, the narrowest at which the full line kept a
    // usable title beside its fixed columns with the scroll bar showing, and the status chip column (72 DIP, "task status
    // shows on the bar and in the tasks window", 2026-11-23) raised that minimum to 692. A full line below it would
    // squeeze the title under the width the window was built to guarantee, so the slim line takes over from there.
    public const double SlimBelow = 692;

    // The List / Matrix switch is shown only from this width: the 2 by 2 needs room to be readable.
    public const double MatrixFrom = 780;

    // Keeps a width between the minimum and half the screen, so the panel never swallows the work area. A screen
    // narrower than twice the minimum still gets the minimum: the lower bound wins, not the half.
    public static double Clamp(double width, double screenWidthDips) =>
        Math.Clamp(width, MinWidth, Math.Max(MinWidth, screenWidthDips / 2));

    public static bool Slim(double width) => width < SlimBelow;

    public static bool MatrixAllowed(double width) => width >= MatrixFrom;
}
