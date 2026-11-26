using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// The items of the "Repeat" submenu that both "⋯" menus carry (the tasks panel's, TasksWindow.OpenMenuFor, and the task
// window's, TaskDetailWindow.OpenMenu). Recurring tasks, task 0b78269e, spec
// docs/superpowers/specs/2026-11-26-recurring-tasks-design.md. One builder for both so the two menus cannot drift apart,
// as SnoozeItems and AssignItems keep the moon's menu and the "⋯" submenu in step.
//
// The items only say WHAT was asked, as the words `task repeat <id> ...` takes ("every 1h", "daily 09:00", "off", or what
// Custom... was given): TasksBoard.Repeat parses them with the one parser (TaskRepeats.ParseRequest) and picks the folder,
// so the menu and the control pipe cannot disagree about what a schedule is.
static class RepeatMenu
{
    /// <summary>The submenu's header on a line: "Repeats" while it repeats, so the line's menu says so without opening it.</summary>
    public static string Header(string? current) => current is null ? "Repeat" : "Repeats";

    /// <summary>
    /// The five choices (<see cref="TaskRepeats.MenuChoices"/>, the current schedule ticked), "Custom…" and, only while the
    /// task repeats, "Off". <paramref name="current"/> is the repeat's schedule text (TaskRowView.RepeatText), or null.
    /// <paramref name="askCustom"/> asks for the CLI text (a PromptDialog in the app, a fake in the tests), prefilled with
    /// <paramref name="current"/>; none answered, nothing happens.
    /// </summary>
    public static List<object> Items(Guid id, string? current, Action<Guid, string> repeat, Func<string, Maybe<string>> askCustom)
    {
        var items = TaskRepeats.MenuChoices
            .Select(choice => (object)Item(choice.Label, () => repeat(id, choice.Text), ticked: choice.Text == current))
            .ToList();
        // A repeat that is none of the five (set from the CLI or by Custom...) ticks Custom…, so the submenu always shows
        // where the task stands.
        var custom = current is not null && TaskRepeats.MenuChoices.All(choice => choice.Text != current);
        items.Add(Item("Custom…", () => askCustom(current ?? "").Tap(words => repeat(id, words)), ticked: custom));
        if (current is not null) items.Add(Item("Off", () => repeat(id, "off")));
        return items;
    }

    // A TextBlock header, so an underscore in a word is not read as an access key (as the windows' own Item helpers).
    static MenuItem Item(string text, Action click, bool ticked = false)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text }, IsChecked = ticked };
        item.Click += (_, _) => click();
        return item;
    }
}
