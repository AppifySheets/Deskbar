namespace TaskSpaces.Core.Domain;

// How many lines the current workspace's window list keeps open however few windows it holds.
//
// Petre: "current workspace should be at least 5 slots tall, so that windows can move in and out without
// causing the size change for the workspace", then "make that number 5 configurable". The list is one window
// per line (the titled list, see FloatingBar.GroupRow), so it grew a line for every window that arrived and
// shrank for every one that left, and everything below it moved each time. Below this many windows it now
// holds empty lines instead; past it, it grows as before, since hiding a window is worse than a taller row.
//
// Pure and in Core, like BarText, so the clamp is testable without constructing a window.
public static class WindowListLines
{
    // Petre's number. Null in state.json means this, so changing it here reaches anyone who never set it.
    public const int Default = 5;

    // One is the list as it always was: a line per window and nothing held. Twelve is about a 4K screen's
    // worth of bar at 150% beside a dozen workspace rows; past that the floor is the bar.
    public const int Minimum = 1;
    public const int Maximum = 12;

    public static int Clamp(int? value) =>
        value is { } lines ? Math.Clamp(lines, Minimum, Maximum) : Default;
}
