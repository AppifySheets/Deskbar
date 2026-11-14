namespace Deskbar.Core.QuickFind;

// WHAT PETRE TYPED INTO QUICK FIND, split into the pieces every result has to contain.
//
// Petre: "inv 10 should find that", of a tab titled "Invoice 2026-10". So the text is split on spaces and a
// result matches when EVERY piece appears somewhere in it, in any order, ignoring case: "inv 10" and "10 inv"
// both find it. Plain "contains", deliberately not fuzzy and not learned: a keyboard finder is only trusted
// when the same letters always find the same things
// (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Matching").
//
// An empty query (nothing typed, or only spaces) matches nothing, so the list stays empty until there is
// something to look for.
public sealed record FindQuery(IReadOnlyList<string> Pieces)
{
    public static FindQuery Parse(string text) =>
        new(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public bool IsEmpty => Pieces.Count == 0;

    public bool Matches(string haystack) =>
        !IsEmpty && Pieces.All(piece => haystack.Contains(piece, StringComparison.OrdinalIgnoreCase));

    // Whether ANY piece appears in the text. Ranking asks this of a tab's own title and workspace, so a tab is
    // not found through the title of the window holding it alone (see FindRanking).
    public bool MatchesAny(string haystack) =>
        Pieces.Any(piece => haystack.Contains(piece, StringComparison.OrdinalIgnoreCase));

    // Whether what was typed is the program's NAME and not just letters inside it: every piece is a whole word
    // of the text. "code" names "Visual Studio Code" (and its exe, Code); "inv" does not name "Invoice Ninja".
    // Only this lets a row that opens a new window take the top of the list, because a half-typed word is far
    // more often the start of a title than a request for another window.
    public bool Names(string program)
    {
        var words = new string(program.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !IsEmpty && Pieces.All(words.Contains);
    }

    // For ranking: a title that STARTS with what was typed first is a better guess than one that merely
    // contains it ("inv" prefers "Invoices 2026" to "My invoices"). Only the first piece, because that is the
    // word Petre began with, the one he thinks of as the name.
    public bool StartsWithFirstPiece(string title) =>
        !IsEmpty && title.TrimStart().StartsWith(Pieces[0], StringComparison.OrdinalIgnoreCase);
}
