namespace Deskbar.Core.Tasks;

/// <summary>What an add box's text makes: the new task's title, and the text of its description (empty for none).</summary>
public sealed record TaskNoteParts(string Title, string Body);

// A NOTE OF SEVERAL LINES (task 98420dbc, Petre on the bar's "Add to Deskbar…" box: "multiline should be allowed; longer
// text should increase the size of the box"). Both add boxes, the bar's and the tasks panel's, take Shift+Enter as a line
// break now, while Enter still adds the note.
//
// The first line that says anything is the TITLE, and every line after it is the DESCRIPTION. A title of several lines
// was the other choice, and it was turned down: every place a title goes is one line (the bar's task line, the tasks
// panel's line, `task list`, a session's briefing, the Windows Terminal tab a session opens in), so a second line would be
// cut off or run into the first everywhere. A description is where longer text already lives, the panel shows it under
// the title, and a session reads it with the task. A one-line note is a title alone, exactly as before.
public static class TaskNote
{
    /// <summary>
    /// The text of an add box taken apart: the first line with anything on it, trimmed, is the title, and the lines after
    /// it are the body, with blank lines at either end dropped but the lines' own indentation kept. With only pictures
    /// pasted (nothing typed) the title is "Picture" or "Pictures" (<see cref="TaskImages.TitleFor"/>).
    /// </summary>
    public static TaskNoteParts Split(string text, int pictures)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .ToArray();
        var body = lines.Skip(1).SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse();
        return new TaskNoteParts(
            TaskImages.TitleFor(lines.FirstOrDefault()?.Trim() ?? "", pictures),
            string.Join("\n", body.Select(line => line.TrimEnd())));
    }
}
