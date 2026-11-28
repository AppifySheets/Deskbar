using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TASKS PANEL FROM THE KEYBOARD ALONE, the pure half (Deskbar task b61f79de; design
// docs/superpowers/specs/2026-11-28-keyboard-navigation-design.md). Petre: "make it possible to navigate with the
// keyboard only", and "i also want to have a panel which will tell me how to navigate".
//
// The panel's buttons stay unfocusable on purpose (TasksWindow.Flat: a click must never take the keyboard from a box he
// is typing in) and every redraw rebuilds them, so the keys do not walk WPF focus. They move the board's own selection,
// which survives every redraw, open the menus the "⋯" buttons open, and call the same actions a click calls. What is
// pure about that lives here, so it is tested without a window:
//
//   Step   which line Up, Down, Home or End selects, given the lines in the order drawn and the one selected now;
//   For    what the keys card lists for the place the panel shows (the card is TasksWindow.KeysCard.cs).

/// <summary>Where the panel is, for the keys card: each place has its own set of keys that act.</summary>
public enum PanelPlace { InboxList, InboxPage, List, Matrix, Stats }

/// <summary>One row of the keys card: the key or keys, and what they do, in his words.</summary>
public sealed record PanelKey(string Keys, string Does);

public static class PanelKeys
{
    /// <summary>Up and Down: one line back or on.</summary>
    public const int Previous = -1;
    public const int Next = 1;

    /// <summary>Home and End: the first and the last line.</summary>
    public const int First = int.MinValue;
    public const int Last = int.MaxValue;

    /// <summary>
    /// The line Up (<see cref="Previous"/>), Down (<see cref="Next"/>), Home (<see cref="First"/>) or End
    /// (<see cref="Last"/>) selects. It stops at the ends rather than wrapping: a wrap from the last line to the first is
    /// a jump the eye does not follow in a long list. With nothing selected, or a selection the view does not draw (a
    /// filter or another tab hides it), Down and Home start at the first line and Up and End at the last, as a list box
    /// does. Nothing drawn, nothing to select.
    /// </summary>
    public static Maybe<Guid> Step(IReadOnlyList<Guid> shown, Maybe<Guid> selected, int by)
    {
        if (shown.Count == 0) return Maybe<Guid>.None;
        if (by == First) return shown[0];
        if (by == Last) return shown[^1];
        var at = selected.HasValue ? shown.ToList().IndexOf(selected.Value) : -1;
        if (at < 0) return by < 0 ? shown[^1] : shown[0];
        return shown[Math.Clamp(at + by, 0, shown.Count - 1)];
    }

    // The letters answer to Alt+letter too: on a page the keyboard is in the comment box, where a plain letter is typed
    // (TasksWindow.Keys.cs). Every key of the panel with the places it acts in, in the order the card lists them: moving first, then opening and
    // leaving, then switching, then acting on the task, then the card itself last so it is always where the eye ends.
    static readonly (PanelKey Key, PanelPlace[] In)[] All =
    [
        (new("Up / Down", "Select the line above or below"), [PanelPlace.InboxList, PanelPlace.List, PanelPlace.Matrix]),
        (new("Up / Down, Page Up / Page Down", "Scroll the page"), [PanelPlace.InboxPage]),
        (new("Home / End", "First or last line"), [PanelPlace.InboxList, PanelPlace.List, PanelPlace.Matrix]),
        (new("Enter", "Open the task's page"), [PanelPlace.InboxList]),
        (new("Enter", "Open or fold the thread"), [PanelPlace.List, PanelPlace.Matrix]),
        (new("Esc", "Back, fold, or close a box"), Everywhere),
        (new("Left / Right", "The tab before or after"), [PanelPlace.InboxList]),
        (new("Ctrl+1 to Ctrl+4", "Inbox, List, Matrix, Stats"), Everywhere),
        (new("Menu key or Shift+F10", "The task's ⋯ menu"), OnATask),
        (new("C or Alt+C", "Write a comment"), [PanelPlace.InboxPage, PanelPlace.List, PanelPlace.Matrix]),
        (new("P or Alt+P", "Open its pull request"), OnATask),
        (new("W or Alt+W", "Move it to another workspace"), OnATask),
        (new("R or Alt+R", "Recheck its state"), OnATask),
        (new("N or Alt+N", "Add a task"), Everywhere),
        (new("F2", "Rename the task"), OnATask),
        (new("Ctrl+F", "Search"), Everywhere),
        (new("? or F1", "These keys"), Everywhere),
    ];

    static PanelPlace[] Everywhere => Enum.GetValues<PanelPlace>();

    // The places where a task is at hand: a selected line, or the page that is open. Not Stats, which has no lines.
    static PanelPlace[] OnATask => [PanelPlace.InboxList, PanelPlace.InboxPage, PanelPlace.List, PanelPlace.Matrix];

    /// <summary>The keys card's rows for the place shown: only the keys that act there, in the card's order.</summary>
    public static IReadOnlyList<PanelKey> For(PanelPlace place) =>
        All.Where(k => k.In.Contains(place)).Select(k => k.Key).ToArray();
}
