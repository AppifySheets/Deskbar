using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A QUESTION ASKED IN CLAUDE CODE'S OWN DIALOG BECOMES THE TASK'S QUESTION (task 493b7ba9, Petre: "i don't see it as a
// question in deskbar", with a screenshot of a session's multiple-choice dialog).
//
// A session asks Petre in two ways. `task ask` puts the question on its task, which then waits on him at the top of the
// bar. The AskUserQuestion tool instead opens a dialog in the session's terminal and blocks until he picks an answer, and
// Deskbar heard nothing of it: the task stayed In progress and Claude's, so nothing on the bar said a session was stuck
// on him. Deskbar now hooks that tool (ClaudeHooks, PreToolUse and PostToolUse with the matcher AskUserQuestion): before
// the dialog opens, its questions become the task's question; once it is answered, the question is settled again.
//
// This file turns the tool's input into that text. tool_input.questions is an array of
// { question, header, options: [{ label, description }], multiSelect }. Each question becomes one line: the question
// itself, then its options' labels, so the bar says what is being decided without opening the terminal. The first words
// say where to answer (the CLAUDE.md rule for asks: what Petre must do comes first). The descriptions are left out:
// they are long, and the dialog shows them.
//
// WHERE TO ANSWER IS NOT ALWAYS THE TERMINAL (task 28fd8550, Petre: "it shouldn't ask me to do it in the terminal if the
// question can be answered from the chat"). Since task c047d0a0 a well-formed dialog also goes on a card on the task's
// page (DialogAsk, TasksWindow.DialogCard), so "Answer in the session's terminal" sent him to the one place he no longer
// needed. Worse, sessions copied the words: a `task ask` is answered on the task's page by its nature, yet other sessions
// began their asks with "Answer in the session's terminal:", "Decide in ..." and "Pick in ...", having read the lead on
// other tasks. So there are two leads now: PageLead while the card can draw the dialog (RemoteControl.DialogAsked decides,
// from the same rules the card's hook uses), Lead when only the terminal can. The card is held for about ten minutes
// (PermissionDesk.HeldFor) and can be handed back; once it is gone while the dialog still waits, the question is reworded
// to Lead (ToTerminal, RemoteControl.CardsGone), so neither lead ever points where the answer cannot go.
public static class DialogQuestion
{
    /// <summary>The tool Claude Code runs for its multiple-choice dialog, and the matcher Deskbar's hook entries use.</summary>
    public const string Tool = "AskUserQuestion";

    /// <summary>The first words of a dialog question only the session's terminal can answer.</summary>
    public const string Lead = "Answer in the session's terminal:";

    /// <summary>The first words of a dialog question whose card is on the task's page (task 28fd8550).</summary>
    public const string PageLead = "Answer on the task's page:";

    /// <summary>
    /// The task question for an AskUserQuestion tool_input, or none when it holds no question text (an odd shape, which
    /// then changes nothing on the task). <paramref name="onPage"/>: the card on the task's page can take the answer, so
    /// the question starts with <see cref="PageLead"/> instead of <see cref="Lead"/>.
    /// </summary>
    public static Maybe<string> From(JsonElement toolInput, bool onPage = false) =>
        toolInput.ValueKind == JsonValueKind.Object
        && toolInput.TryGetProperty("questions", out var questions)
        && questions.ValueKind == JsonValueKind.Array
        && questions.EnumerateArray().Select(Line).Where(l => l.Length > 0).ToArray() is { Length: > 0 } lines
            ? Led(onPage ? PageLead : Lead, lines)
            : Maybe<string>.None;

    /// <summary>
    /// A page-led dialog question with the terminal's lead instead, for when its card left the page while the dialog still
    /// waits; none for any other text (a terminal-led question, or a session's own `task ask`).
    /// </summary>
    public static Maybe<string> ToTerminal(string question) =>
        question.StartsWith(PageLead, StringComparison.Ordinal)
            ? Lead + question[PageLead.Length..]
            : Maybe<string>.None;

    /// <summary>
    /// A session's own `task ask` text without a dialog lead it copied (task 28fd8550: sessions began their asks with
    /// "Answer in the session's terminal:", read off dialog questions on other tasks). A `task ask` is answered on the
    /// task's page, so neither lead belongs on it, and keeping PageLead to dialog questions is what lets
    /// RemoteControl.CardsGone tell a dialog's question from a session's ask after a restart. Text that is only a lead is
    /// kept as it is.
    /// </summary>
    public static string WithoutLead(string text) =>
        new[] { Lead, PageLead }.FirstOrDefault(l => text.TrimStart().StartsWith(l, StringComparison.Ordinal)) is { } lead
        && text.TrimStart()[lead.Length..].TrimStart() is { Length: > 0 } rest
            ? rest
            : text;

    // One question follows the lead on its line; several are a list under it.
    static string Led(string lead, string[] lines) =>
        lines.Length == 1
            ? $"{lead} {lines[0]}"
            : $"{lead}\n{string.Join("\n", lines.Select(l => $"- {l}"))}";

    // One question: its text, then "(label / label / ...)" when it has options. "" for an entry with no question text.
    static string Line(JsonElement question) =>
        Text(question, "question") is { } text
            ? Labels(question) is { Length: > 0 } labels ? $"{text} ({string.Join(" / ", labels)})" : text
            : "";

    static string[] Labels(JsonElement question) =>
        question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
            ? options.EnumerateArray().Select(o => Text(o, "label")).OfType<string>().ToArray()
            : [];

    // A non-empty string property, trimmed, or null (also for an element that is not an object).
    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;
}
