using System.Globalization;
using System.Text;

namespace Deskbar.Core.Tasks;

// WHAT THE PERMISSION CARD SAYS, pure (Allow / Deny from a task's page). The user allows exactly what this shows, so it
// is built to one rule: NOTHING OF THE REQUEST IS HIDDEN. Four ways a card could hide part of a command, and what this
// does about each:
//
//   * A character that draws as nothing, or as something else. Control characters (ESC, a bare CR, a tab), the Unicode
//     format characters (the bidi overrides U+202A-202E and isolates U+2066-2069 behind "Trojan source" attacks, zero
//     width spaces and joiners, the BOM, tag characters), line and paragraph separators, a lone surrogate and the
//     invisible fillers and variation selectors are each written as a visible mark, ⟨U+202E⟩. Only LF stays a line
//     break. The two bracket characters of the mark are marked too, so a command cannot imitate a mark with plain text.
//   * A field nobody looks at. Every field of tool_input is a part of the card, in Claude Code's order; the tool's main
//     field (a shell command, an edited file's path) comes first and is marked Main, the rest follow it.
//   * A cut line. A line is never shortened here: the drawing wraps it (the mockup's sideways-scrolling box is the
//     thing the plan forbids, since it hid the tail of a long command off the right edge).
//   * A long input. Fold splits it by WHOLE lines into the lines shown and the rest, with the rest's count for "Show
//     all (N more lines)"; the two halves together are every line, in order.
//
// Claude's own `description` of a shell command is model-written and may not match the command, so it is not a part:
// it is ClaudeSays, which the card shows as secondary text labelled as Claude's words.
public static class PermissionDisplay
{
    /// <summary>How many lines of a part the card shows before "Show all".</summary>
    public const int FoldLines = 12;

    // The field each tool's card leads with. Bash and PowerShell were both seen for shell commands (spike); the edit
    // tools lead with the file they change. Any other tool, MCP tools included, leads with its first field.
    static readonly Dictionary<string, string[]> MainFields = new(StringComparer.Ordinal)
    {
        ["Bash"] = ["command"],
        ["PowerShell"] = ["command"],
        ["Edit"] = ["file_path"],
        ["MultiEdit"] = ["file_path"],
        ["Write"] = ["file_path"],
        ["Read"] = ["file_path"],
        ["NotebookEdit"] = ["notebook_path"],
        ["WebFetch"] = ["url"],
    };

    const string ClaudesWords = "description";

    /// <summary>The card's text for a request: every field, the main one first, and Claude's own description apart.</summary>
    public static PermissionText Of(PermissionRequest request)
    {
        var mains = MainFields.TryGetValue(request.Tool, out var named) ? named : [];
        var shell = mains.Length > 0 && mains[0] == "command";
        // A shell command's description is Claude's words about it; for any other tool a field of that name is just a field.
        var says = shell ? request.Fields.FirstOrDefault(f => f.Name == ClaudesWords) : null;
        var rest = request.Fields.Where(f => !ReferenceEquals(f, says)).ToList();
        var main = rest.FirstOrDefault(f => mains.Contains(f.Name)) ?? (mains.Length == 0 ? rest.FirstOrDefault() : null);
        var parts = (main is null ? rest : [main, .. rest.Where(f => !ReferenceEquals(f, main))])
            .Select(f => new PermissionPart(f.Name, Visible(f.Value), ReferenceEquals(f, main)))
            .ToList();
        return new PermissionText(Visible(request.Tool), Visible(request.Folder), parts,
            says is null ? null : Visible(says.Value));
    }

    /// <summary>
    /// The text with every character that could hide or disguise part of it written as a visible mark (see the header).
    /// Every other character is kept as it is, so nothing is dropped.
    /// </summary>
    public static string Visible(string text)
    {
        var shown = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') { shown.Append('\n'); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var rune = new Rune(c, text[i + 1]);
                if (Hidden(rune.Value, Rune.GetUnicodeCategory(rune))) shown.Append(Mark(rune.Value));
                else shown.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            // A lone surrogate is not a character at all; marked by its code unit rather than replaced, so it is not lost.
            if (char.IsSurrogate(c) || Hidden(c, char.GetUnicodeCategory(c))) shown.Append(Mark(c));
            else shown.Append(c);
        }
        return shown.ToString();
    }

    static string Mark(int codePoint) => $"⟨U+{codePoint:X4}⟩";

    static bool Hidden(int codePoint, UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        // The mark's own brackets, so plain text cannot pass for a mark.
        || codePoint is 0x27E8 or 0x27E9
        // Fillers that draw as blank space and the combining grapheme joiner.
        || codePoint is 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x034F
        // Variation selectors, which change how the character before them looks.
        || codePoint is >= 0xFE00 and <= 0xFE0F
        || codePoint is >= 0xE0100 and <= 0xE01EF;

    /// <summary>
    /// A part's text split by whole lines: the first <paramref name="lines"/> lines to show, and the rest behind "Show
    /// all". Shown followed by Rest is every line of the text, in order; no line is ever cut.
    /// </summary>
    public static PermissionFold Fold(string text, int lines = FoldLines)
    {
        var all = text.Split('\n');
        return all.Length <= lines ? new PermissionFold(all, []) : new PermissionFold(all[..lines], all[lines..]);
    }
}

/// <summary>The card's text: the tool, the session's folder, every field of the input, and Claude's own words apart.</summary>
public sealed record PermissionText(string Tool, string Folder, IReadOnlyList<PermissionPart> Parts, string? ClaudeSays);

/// <summary>One field of the input as the card shows it: its name, its text with hidden characters marked, and whether it leads.</summary>
public sealed record PermissionPart(string Label, string Text, bool Main);

/// <summary>A part split by whole lines: what shows at first, and what "Show all" adds (its count is the button's number).</summary>
public sealed record PermissionFold(IReadOnlyList<string> Shown, IReadOnlyList<string> Rest);
