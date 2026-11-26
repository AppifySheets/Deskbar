using System.Text;

namespace Deskbar.Core.Bookmarks;

// The stand-in for a link bookmark's icon while its favicon is missing: a tile with the first letter of the
// bookmark's name on a colour taken from its host. Same host, same colour, every run and every machine.
public static class LetterTile
{
    // Letter: the first letter or digit of the name, upper-cased; '?' when there is none ("--").
    // Hue: 0 to 359 degrees, from an FNV-1a hash of the lower-cased host. string.GetHashCode is NOT used: it is
    // randomised per process, so the colour would change at every start.
    public static (char Letter, double Hue) Of(string name, string host) =>
        (name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).DefaultIfEmpty('?').First(),
         Fnv1a(host.ToLowerInvariant()) % 360);

    // 32-bit FNV-1a over the UTF-8 bytes, the standard offset basis and prime.
    static uint Fnv1a(string text) =>
        Encoding.UTF8.GetBytes(text).Aggregate(2166136261u, (hash, b) => (hash ^ b) * 16777619u);
}
