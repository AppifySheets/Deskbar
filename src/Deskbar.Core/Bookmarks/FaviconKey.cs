namespace Deskbar.Core.Bookmarks;

// The file-name key a link's cached favicon is stored under: the ORIGIN (scheme, host, port), not the full URL,
// because the icon is fetched once per origin (<origin>/favicon.ico) and every bookmark on that origin shares it.
// "https://Portainer.local:9443/x" -> "https_portainer.local_9443".
//
// The host is the IDN (punycode) form so a non-ASCII host gives a plain file name, and the whole key is lower
// case so two spellings of one origin do not cache twice. Anything a file name cannot hold (an IPv6 literal's
// colons, say) becomes '-'.
public static class FaviconKey
{
    public static string Of(Uri origin) =>
        string.Concat($"{origin.Scheme}_{origin.IdnHost}_{origin.Port}".ToLowerInvariant()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
