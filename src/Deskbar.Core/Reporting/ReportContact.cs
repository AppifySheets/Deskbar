using System.Text.RegularExpressions;

namespace Deskbar.Core.Reporting;

/// <summary>What the "How can we reach you?" line holds, as far as a quick look can tell.</summary>
public enum ContactKind
{
    /// <summary>An email address, a Telegram handle or link, or a phone number: something we can write to.</summary>
    Reachable,
    /// <summary>Nothing typed. The report still goes, but nobody can ask a follow-up question.</summary>
    Missing,
    /// <summary>Something typed that looks like none of the above, most often a typo in an address.</summary>
    Unrecognised,
}

// The contact line stays optional (Petre, 2026-11-22: making it mandatory gets typed junk like "a@a.com" and loses
// reports from people who will not give one). Instead the report window asks ONCE when it is empty or does not look
// like an address, and a second Send goes anyway. This is the "does it look like an address" half.
//
// Deliberately loose: a false "unrecognised" costs the user one extra click, but a check that is too strict would
// argue with a real address. It is a typo catcher, not validation; nothing is refused on its answer.
public static partial class ReportContact
{
    // One @, something on both sides, a dot in the domain, no spaces. "petre@deskbar" and "petre.deskbar.app" miss.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s.]{2,}$")]
    private static partial Regex Email();

    // A Telegram username is 5 to 32 letters, digits and underscores. Accepted as "@name" or a t.me link; a bare
    // word without the @ is not, since "asdf" or "none" would pass as one.
    [GeneratedRegex(@"^(@|(https?://)?t\.me/)[A-Za-z0-9_]{5,32}$", RegexOptions.IgnoreCase)]
    private static partial Regex Telegram();

    // A phone number, which Telegram and WhatsApp both reach: an optional +, then 7 to 15 digits, spaces, dashes,
    // dots or brackets allowed between them.
    [GeneratedRegex(@"^\+?[\d\s().-]{7,25}$")]
    private static partial Regex Phone();

    static int Digits(string value) => value.Count(char.IsDigit);

    /// <summary>Sorts the contact line as typed (it is trimmed here) into reachable, missing or unrecognised.</summary>
    public static ContactKind Check(string contact) => contact.Trim() switch
    {
        "" => ContactKind.Missing,
        var value when Email().IsMatch(value) || Telegram().IsMatch(value) => ContactKind.Reachable,
        var value when Phone().IsMatch(value) && Digits(value) is >= 7 and <= 15 => ContactKind.Reachable,
        _ => ContactKind.Unrecognised,
    };
}
