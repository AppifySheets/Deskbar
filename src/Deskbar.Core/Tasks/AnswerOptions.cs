using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE ANSWER OPTIONS OF A QUESTION (the inbox's answer buttons): a session that asks the user something can name the
// few answers it expects ("Merge it", "Not yet"), so the user answers with one click instead of typing a comment.
// This file holds the rules for the list and the one place the list is read from a session's ask; TaskBoard.Ask is the
// only place one is stored (TaskItem.QuestionOptions), and the buttons are somebody else's drawing.
//
// THE LIMITS are small on purpose: a button is a short word or two ("Merge it"), a row of them has to fit under one
// question, and a question that needs a paragraph per answer is a conversation, which a typed comment is for. At most
// MaxCount options, each at most MaxLength characters after trimming, no line breaks (an option is one line of a
// button), none empty, and no two alike in any case (two buttons that read the same cannot be told apart, and the
// second would answer exactly as the first). A list that breaks any rule is refused WHOLE, naming the limit, and the
// ask stores nothing: a session that sent five options and got four buttons would be answered in a way it never offered.
//
// WHY THE OPTIONS GO ON STDIN, AFTER A MARKER LINE, and not as `--option <text>` on the command line (the brief's first
// form). An option is free text, and free text never goes on cmd's command line (ControlArguments, "FREE TEXT NEVER
// GOES THROUGH cmd's COMMAND LINE": deskbar.cmd hands its arguments to cmd with %*, a quote in the text ends the quoted
// run and whatever follows is cmd's to run, measured with `task status-line 'checking "x & echo INJECTED" now'`). A
// button's text is exactly that kind of text ("Merge & delete the branch", `Say "no"`). stdin is the one text channel,
// and it is taken by the question already, so the options are more lines of it: a line that is exactly `--options`
// ends the question, and every line after it is one option. The client reads stdin once and sends the whole text as
// the ask's one text word, so no change was needed in how the client handles `-`, and the split happens here, on the
// server, where the limits are checked too. A session that sends no marker asks as before.
//
//   task ask - --session <id> <<'EOF'
//   Merge [PR 512](https://github.com/owner/repo/pull/512)?
//   --options
//   Merge it
//   Not yet
//   EOF
//
// The marker is a line of its own and is looked for from the top: the FIRST such line splits the text, so a question
// that quotes the marker inside a later option is not cut twice. A question that really contains a line `--options`
// by itself is the one text that cannot be asked as it stands (the line would start an option list); that is the price
// of a marker that cannot be mistaken for prose, and the refusal names the marker when what follows it is not a
// valid list. Blank lines among the options are skipped, since a heredoc often ends with one.
public static class AnswerOptions
{
    /// <summary>How many options a question can carry.</summary>
    public const int MaxCount = 4;

    /// <summary>How many characters one option can have, after trimming.</summary>
    public const int MaxLength = 40;

    /// <summary>The line of its own that ends a question's text and starts its options.</summary>
    public const string Marker = "--options";

    /// <summary>What every refusal ends with, so the session sees the limits whichever one it broke.</summary>
    public static readonly string Limits =
        $"A question takes at most {MaxCount} options, each at most {MaxLength} characters on one line and different from the others (in any case).";

    /// <summary>
    /// The text of a session's ask split into its question and its options. No marker line: the text is the question and
    /// there are no options. A marker with nothing valid after it is refused, never read as no options, so a session
    /// that meant buttons is told it got none.
    /// </summary>
    public static Result<(string Question, IReadOnlyList<string> Options)> Split(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var at = Array.FindIndex(lines, line => line.Trim() == Marker);
        if (at < 0) return Result.Success<(string, IReadOnlyList<string>)>((text ?? "", []));
        var given = lines.Skip(at + 1).Where(line => line.Trim().Length > 0).ToArray();
        return given.Length == 0
            ? Result.Failure<(string, IReadOnlyList<string>)>($"Options refused, nothing was asked: there is no option after the {Marker} line. {Limits}")
            : Check(given).Map(options => (string.Join("\n", lines.Take(at)), options));
    }

    // Any character that ends a line: an option is one line of a button.
    static bool HasLineBreak(string option) => option.Any(c => c is '\r' or '\n' or (char)0x85 or (char)0x2028 or (char)0x2029);

    /// <summary>
    /// The options trimmed and checked against the limits; the whole list is refused for the first one that breaks a
    /// rule. Used by Split and again by TaskBoard.Ask, so a caller that skips the text form (the tests, a later caller)
    /// meets the same rules.
    /// </summary>
    public static Result<IReadOnlyList<string>> Check(IReadOnlyList<string>? options)
    {
        var trimmed = (options ?? []).Select(o => (o ?? "").Trim()).ToArray();
        return trimmed.Length > MaxCount ? Refused($"{trimmed.Length} options is too many")
            : trimmed.FirstOrDefault(o => o.Length == 0) is not null ? Refused("An option cannot be empty")
            : trimmed.FirstOrDefault(o => o.Length > MaxLength) is { } longest ? Refused($"'{Shown(longest)}' is {longest.Length} characters")
            : trimmed.FirstOrDefault(HasLineBreak) is { } broken ? Refused($"'{Shown(broken)}' has a line break")
            : trimmed.GroupBy(o => o, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice ? Refused($"'{Shown(twice.Key)}' is given twice")
            : Result.Success<IReadOnlyList<string>>(trimmed);
    }

    static Result<IReadOnlyList<string>> Refused(string what) =>
        Result.Failure<IReadOnlyList<string>>($"Options refused, nothing was asked: {what}. {Limits}");

    // An option in a refusal, cut so one long line cannot flood the message.
    static string Shown(string option) => option.Length > MaxLength + 10 ? option[..(MaxLength + 10)] + "..." : option;
}
