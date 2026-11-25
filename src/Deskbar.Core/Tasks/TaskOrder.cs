namespace Deskbar.Core.Tasks;

// THE TWO FLAGS AND COVEY'S QUADRANTS. Design: progress.md Rulings 24, 25 and 28 (task-12-brief.md), after the spec
// docs/superpowers/specs/2026-11-22-tasks-board-design.md. Petre: "tasks should be draggable so i move the ones that
// aren't important below", "they should have importance - important and urgent, two flags", and "do the quadrants as
// in covey's book".
//
// A task carries two flags, Important and Urgent. Together they name one of the four quadrants of Covey's time
// management matrix (The 7 Habits of Highly Effective People, habit 3), numbered as the book numbers them:
//
//                    Urgent      Not urgent
//   Important        I           II
//   Not important    III         IV
//
// The open tasks are shown Waiting on you first (pinned: the questions, then the Done tasks, each a new one on top and otherwise in the order
// Petre drags it to, TaskItem.Pin), then quadrant I, II, III and IV,
// each in an order Petre sets by dragging. That order is a number on each task, its Rank: smaller is higher. TaskBoard
// keeps the rules (TaskBoard.Place, SetFlag, Group); this file holds the vocabulary.
//
// The enum's numbering follows the book, so `Quadrant.I` reads as "quadrant one" wherever it is used.

/// <summary>Covey's four quadrants, I to IV in the book's numbering (see the header).</summary>
public enum Quadrant { I, II, III, IV }

/// <summary>The two flags a task carries; the toggles on each line and `task flag` set them.</summary>
public enum TaskFlag { Important, Urgent }

/// <summary>Turns two flags into a quadrant and back.</summary>
public static class QuadrantOf
{
    /// <summary>The quadrant two flags name.</summary>
    public static Quadrant Flags(bool important, bool urgent) =>
        (important, urgent) switch
        {
            (true, true) => Quadrant.I,
            (true, false) => Quadrant.II,
            (false, true) => Quadrant.III,
            _ => Quadrant.IV,
        };

    public static bool IsImportant(this Quadrant quadrant) => quadrant is Quadrant.I or Quadrant.II;

    public static bool IsUrgent(this Quadrant quadrant) => quadrant is Quadrant.I or Quadrant.III;

    /// <summary>The quadrant with one flag set or cleared.</summary>
    public static Quadrant With(this Quadrant quadrant, TaskFlag flag, bool on) =>
        flag == TaskFlag.Important ? Flags(on, quadrant.IsUrgent()) : Flags(quadrant.IsImportant(), on);

    /// <summary>The book's numeral, "I" to "IV": what `task list` prints and the window's captions start with.</summary>
    public static string Numeral(this Quadrant quadrant) => quadrant.ToString();

    /// <summary>
    /// Covey's own words for each quadrant, as the window's List view captions them (Ruling 28): "Urgent and
    /// important", "Important, not urgent", "Urgent, not important", "Not urgent, not important".
    /// </summary>
    public static string Words(this Quadrant quadrant) => quadrant switch
    {
        Quadrant.I => "Urgent and important",
        Quadrant.II => "Important, not urgent",
        Quadrant.III => "Urgent, not important",
        _ => "Not urgent, not important",
    };
}

/// <summary>
/// Where in its quadrant a task goes: the top, the end, a 1-based position (`task order &lt;id&gt; &lt;n&gt;`, clamped
/// to the group), or just before or after another task of that quadrant (what a drop says: the line it landed on).
/// </summary>
public abstract record TaskPlace
{
    TaskPlace() { }

    public static readonly TaskPlace Top = new TopPlace();
    public static readonly TaskPlace End = new EndPlace();

    /// <summary>The <paramref name="position"/>th place in the group, counting from 1; out of range is clamped.</summary>
    public static TaskPlace At(int position) => new AtPlace(position);

    public static TaskPlace Before(Guid task) => new BeforePlace(task);

    public static TaskPlace After(Guid task) => new AfterPlace(task);

    public sealed record TopPlace : TaskPlace;

    public sealed record EndPlace : TaskPlace;

    public sealed record AtPlace(int Position) : TaskPlace;

    public sealed record BeforePlace(Guid Task) : TaskPlace;

    public sealed record AfterPlace(Guid Task) : TaskPlace;
}
