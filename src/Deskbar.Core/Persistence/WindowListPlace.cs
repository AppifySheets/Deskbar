namespace Deskbar.Core.Persistence;

// Where the current workspace's titled window list is drawn. Not stored as such: AppState keeps two bools
// (WindowListAtBottom, WindowListOnScreens) and derives this, so state.json from every older build keeps its
// meaning. See AppState.WindowListPlace.
public enum WindowListPlace
{
    // The workspace's own row expands into the list (the default).
    Row,

    // A separate section at the bottom of the bar, below every row.
    Bottom,

    // A strip along the top of every screen (docs/superpowers/specs/2026-11-19-screen-strips-design.md).
    Screens,
}
