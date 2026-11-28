using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// CLAUDE CODE'S MULTIPLE-CHOICE DIALOG, ANSWERED FROM THE TASK'S PAGE (task c047d0a0, Petre: "model this in the task so
// i don't have to open claude code").
//
// AskUserQuestion opens a dialog in the session's terminal and blocks until it is answered. Until this task Deskbar only
// put the dialog's text on the task (DialogQuestion, task 493b7ba9) and Petre still had to find the terminal. Claude Code
// runs the PermissionRequest hook for this tool too (measured 2026-11-28: permission_mode "auto", tool_input carrying the
// questions), and the hooks reference says an allow decision for AskUserQuestion must carry an updatedInput that
// "echo[es] back the original questions array and add[s] an answers object mapping each question's text to the chosen
// answer". The probe of that day answered "Pick a colour" with Blue from a hook, and the model read
// `"Pick a colour"="Blue"`: the terminal dialog was never needed. So the same hook that carries the Allow / Deny card
// (claude-permission-hook.cmd, PermissionDesk) carries this card's answers.
//
// THE SHAPE (the hooks reference and the Agent SDK's "Handle approvals and user input"): tool_input.questions is 1 to 4
// questions, each with question (the text), header (a short label), multiSelect, and options of label, description and,
// when the session asked for previews, preview. An answer is the picked option's LABEL; several picks are joined with
// ", " (the SDK's own example); a typed "Other" answer is the typed text itself, never the word "Other".
//
// WHAT IS REFUSED, so the terminal keeps the dialog: input that is not this shape, a question with no text or no
// options, and two questions with the same text (the answers are keyed by the text, so they could not be told apart).
public sealed record DialogAsk(IReadOnlyList<DialogAsk.Item> Questions, string Input)
{
    /// <summary>One option of a question: the label sent back when it is picked, its description, and its preview.</summary>
    public sealed record Option(string Label, string Description, Maybe<string> Preview);

    /// <summary>One question: its text (the answer's key), its short header, whether several picks are allowed, its options.</summary>
    public sealed record Item(string Question, string Header, bool MultiSelect, IReadOnlyList<Option> Options);

    /// <summary>How several picks of one multi-select question are joined into its answer (the Agent SDK's example).</summary>
    public const string PickSeparator = ", ";

    /// <summary>The dialog a permission request carries, or none when it is not AskUserQuestion or not a shape the card can show.</summary>
    public static Maybe<DialogAsk> Of(PermissionRequest request) =>
        request.IsAskUserQuestion ? Parse(request.Input) : Maybe<DialogAsk>.None;

    /// <summary>The dialog in an AskUserQuestion tool_input's JSON text, or none (see the header for what is refused).</summary>
    public static Maybe<DialogAsk> Parse(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array) return Maybe<DialogAsk>.None;
            var items = questions.EnumerateArray().Select(ItemOf).ToList();
            return items.Count > 0
                   && items.All(i => i.HasValue)
                   && items.Select(i => i.Value.Question).Distinct(StringComparer.Ordinal).Count() == items.Count
                ? new DialogAsk(items.Select(i => i.Value).ToList(), input)
                : Maybe<DialogAsk>.None;
        }
        catch (JsonException)
        {
            return Maybe<DialogAsk>.None;
        }
    }

    static Maybe<Item> ItemOf(JsonElement question) =>
        question.ValueKind == JsonValueKind.Object
        && Text(question, "question") is { Length: > 0 } text
        && question.TryGetProperty("options", out var options)
        && options.ValueKind == JsonValueKind.Array
        && options.EnumerateArray().Select(OptionOf).ToList() is { Count: > 0 } found
        && found.All(o => o.HasValue)
            ? new Item(text, Text(question, "header") ?? "",
                question.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True,
                found.Select(o => o.Value).ToList())
            : Maybe<Item>.None;

    static Maybe<Option> OptionOf(JsonElement option) =>
        option.ValueKind == JsonValueKind.Object && Text(option, "label") is { Length: > 0 } label
            ? new Option(label, Text(option, "description") ?? "", Maybe.From(Text(option, "preview")))
            : Maybe<Option>.None;

    // A string property as Claude Code sent it (the label must go back byte for byte), or null when absent or empty.
    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// The line the hook prints for these answers (question text to answer), or a failure when a question has no answer
    /// or an answer names a question the dialog does not have. The updatedInput is the whole tool_input as Claude Code
    /// sent it with "answers" set, because it REPLACES the input (the hooks reference). Every non-ASCII character is
    /// written as a \u escape, so the line is pure ASCII and cmd's `type` hands it over unchanged whatever the console's
    /// code page.
    /// </summary>
    public Result<string> AnswerLine(IReadOnlyDictionary<string, string> answers) =>
        Result.SuccessIf(Questions.All(q => answers.TryGetValue(q.Question, out var a) && a.Trim().Length > 0),
                "Answer every question first.")
            .Ensure(() => answers.Keys.All(k => Questions.Any(q => q.Question == k)), "An answer names a question this dialog does not ask.")
            .Map(() =>
            {
                var input = JsonNode.Parse(Input)!.AsObject();
                input["answers"] = new JsonObject(Questions.Select(q =>
                    KeyValuePair.Create<string, JsonNode?>(q.Question, JsonValue.Create(answers[q.Question].Trim()))));
                var line = new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = PermissionRequest.Event,
                        ["decision"] = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input },
                    },
                };
                // The default encoder escapes everything outside ASCII (and HTML-sensitive characters), which is still JSON.
                return line.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            });

    /// <summary>
    /// The answers in an AskUserQuestion tool result ({"questions": [...], "answers": {question: answer}}), or none when
    /// it carries no answer. An answer given as an array of labels is joined with <see cref="PickSeparator"/>; any other
    /// kind of value is left out.
    /// </summary>
    public static Maybe<IReadOnlyDictionary<string, string>> AnswersIn(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("answers", out var answers)
        && answers.ValueKind == JsonValueKind.Object
        && answers.EnumerateObject()
            .Select(a => (a.Name, Text: a.Value.ValueKind switch
            {
                JsonValueKind.String => a.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(PickSeparator, a.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
                _ => "",
            }))
            .Where(a => a.Text.Trim().Length > 0)
            .DistinctBy(a => a.Name, StringComparer.Ordinal)
            .ToDictionary(a => a.Name, a => a.Text, StringComparer.Ordinal) is { Count: > 0 } found
            ? Maybe.From<IReadOnlyDictionary<string, string>>(found)
            : Maybe<IReadOnlyDictionary<string, string>>.None;

    /// <summary>One line per question for the task's thread after an answer: "Header: answer" (the question when no header).</summary>
    public string Summary(IReadOnlyDictionary<string, string> answers) =>
        string.Join("\n", Questions.Where(q => answers.ContainsKey(q.Question))
            .Select(q => $"- {(q.Header.Length > 0 ? q.Header : q.Question)}: {answers[q.Question].Trim()}"));

    /// <summary>Deskbar's line in the task's thread once the dialog is answered, wherever it was answered.</summary>
    public static string ThreadLine(string summary) => $"Answered Claude's question:\n{summary}";
}
