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
public static class DialogQuestion
{
    /// <summary>The tool Claude Code runs for its multiple-choice dialog, and the matcher Deskbar's hook entries use.</summary>
    public const string Tool = "AskUserQuestion";

    /// <summary>The first words of every dialog question on a task, telling Petre where the answer goes.</summary>
    public const string Lead = "Answer in the session's terminal:";

    /// <summary>
    /// The task question for an AskUserQuestion tool_input, or none when it holds no question text (an odd shape, which
    /// then changes nothing on the task).
    /// </summary>
    public static Maybe<string> From(JsonElement toolInput) =>
        toolInput.ValueKind == JsonValueKind.Object
        && toolInput.TryGetProperty("questions", out var questions)
        && questions.ValueKind == JsonValueKind.Array
        && questions.EnumerateArray().Select(Line).Where(l => l.Length > 0).ToArray() is { Length: > 0 } lines
            ? lines.Length == 1
                ? $"{Lead} {lines[0]}"
                : $"{Lead}\n{string.Join("\n", lines.Select(l => $"- {l}"))}"
            : Maybe<string>.None;

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
