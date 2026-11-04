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

    // For ranking: a title that STARTS with what was typed first is a better guess than one that merely
    // contains it ("inv" prefers "Invoices 2026" to "My invoices"). Only the first piece, because that is the
    // word Petre began with, the one he thinks of as the name.
    public bool StartsWithFirstPiece(string title) =>
        !IsEmpty && title.TrimStart().StartsWith(Pieces[0], StringComparison.OrdinalIgnoreCase);
}
