using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tasks;

// The edge of a screen the tasks panel docks on.
public enum PanelEdge { Left, Right }

// Where the panel docks: an edge and the screen (physical pixels, Right and Bottom exclusive) it is an app bar on.
public sealed record PanelPlacement(PanelEdge Edge, WindowRect Screen);

// Which edge of which screen the tasks panel docks on, given how the bar sits (spec 2026-11-23, "Where it docks").
// Pure so the rule can be pinned by tests: the panel's app bar (Task 5) only reads the answer and registers it.
public static class TasksPanelPlacement
{
    // `barEdge` is the bar's dock mode as the app stores it: "left", "right", "seam" (DockAcrossScreens), or null
    // when the bar floats (DockBar off). `barMiddleX` is the bar's horizontal middle in the same coordinates as the
    // screens, used only to pick the nearer edge when the bar floats.
    public static PanelPlacement For(string? barEdge, WindowRect barScreen, Maybe<WindowRect> seamRightScreen, int barMiddleX) =>
        barEdge switch
        {
            // Bar docked left or right: the panel is a second app bar on the SAME edge, registered after the bar's,
            // so the shell places it just inside the bar.
            "left" => new PanelPlacement(PanelEdge.Left, barScreen),
            "right" => new PanelPlacement(PanelEdge.Right, barScreen),

            // Bar across the seam: the panel goes on the right-hand screen's LEFT edge, inside the bar's right half.
            // Without a right-hand screen (it was unplugged) the bar's own screen is the only sane answer.
            "seam" => new PanelPlacement(PanelEdge.Left, seamRightScreen.GetValueOrDefault(barScreen)),

            // Floating bar, or a value nobody writes (a hand-edited state file): the panel cannot sit "beside" a
            // floating bar, so it takes the edge of the bar's screen nearer the bar. A tie goes Right, the side the
            // bar defaults to. Treating unknown strings as floating means a bad value degrades, it never throws.
            _ => new PanelPlacement(
                barMiddleX - barScreen.Left < barScreen.Right - barMiddleX ? PanelEdge.Left : PanelEdge.Right,
                barScreen),
        };
}
