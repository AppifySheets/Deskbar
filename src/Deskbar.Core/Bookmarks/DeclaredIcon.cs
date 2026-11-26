using System.Net;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Bookmarks;

// THE ICON A BOOKMARKED PAGE DECLARES, for the favicon fetch's second try (FaviconFetcher.FetchDeclared in
// Deskbar.Windows). Petre's Portainer answers 404 for /favicon.ico and names its logo in the page instead, with a
// <link rel="icon">, so its chip kept its letter tile. Decided with him (option A, 2026-11-26) and published on
// deskbar.app/privacy, which is why the limits below are strict:
//
//   * only a <link> whose rel CONTAINS "icon" counts: "icon", "shortcut icon", "apple-touch-icon" (and "mask-icon",
//     which is always an svg and so falls to the next rule);
//   * its href is resolved against the page's own address and taken only when it is on the SAME origin (scheme, host
//     and port). A page that points at a CDN or another host gets no icon: the privacy page promises that a bookmark
//     only ever talks to its own site;
//   * an svg is refused (by its extension or a type of image/svg+xml), because WPF cannot decode one, and fetching
//     what cannot be shown is a request for nothing;
//   * the first <link> in page order that passes all of that wins, so a refused mask-icon does not hide the PNG after
//     it. Still only ONE icon is fetched, so the whole fetch stays at three requests at most.
//
// A tolerant scan, not an HTML parser: the page is untrusted, may be cut off at the read cap, and all that is wanted
// from it is one attribute of one tag. Pure, so the decision has tests of its own (DeclaredIconTests).
public static class DeclaredIcon
{
    // A <link ...> tag, up to its closing '>'. Attribute values cannot hold '>' in this scan, which only ever costs an
    // icon on a page that writes one there.
    static readonly Regex LinkTag = new(@"<link\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // name="value", name='value', name=value or a bare name.
    static readonly Regex Attribute = new(@"([^\s=/>""']+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+)))?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // The address the page itself is asked at: the bookmark's link without its #fragment (never sent to a server
    // anyway) and without any user:password, which must never reach the request (no credentials, the privacy page).
    public static Uri PageOf(Uri link) =>
        new UriBuilder(link) { Fragment = "", UserName = "", Password = "" }.Uri;

    // The first acceptable icon the page declares, as an absolute address on the page's origin, or none.
    public static Maybe<Uri> Of(string html, Uri page)
    {
        try
        {
            return LinkTag.Matches(html ?? "")
                .Select(tag => AttributesOf(tag.Groups[1].Value))
                .Where(attributes => attributes.TryGetValue("rel", out var rel) && rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                .Select(attributes => Accepted(attributes, page))
                .FirstOrDefault(icon => icon.HasValue);
        }
        // A pathological page that runs the scan past its timeout is a page with no icon, never an exception.
        catch (RegexMatchTimeoutException)
        {
            return Maybe<Uri>.None;
        }
    }

    // The attributes of one tag, names lower case, values with their entities decoded (&amp; in a query string). A
    // repeated name keeps its first value, as a browser does.
    static IReadOnlyDictionary<string, string> AttributesOf(string inside) =>
        Attribute.Matches(inside)
            .Select(m => (Name: m.Groups[1].Value.ToLowerInvariant(),
                Value: WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value)))
            .GroupBy(a => a.Name)
            .ToDictionary(g => g.Key, g => g.First().Value);

    static Maybe<Uri> Accepted(IReadOnlyDictionary<string, string> attributes, Uri page) =>
        attributes.TryGetValue("href", out var href)
        && !string.IsNullOrWhiteSpace(href)
        && Uri.TryCreate(page, href.Trim(), out var icon)
        && SameOrigin(icon, page)
        && icon.UserInfo.Length == 0
        && !IsSvg(icon, attributes.GetValueOrDefault("type"))
            ? icon
            : Maybe<Uri>.None;

    static bool SameOrigin(Uri icon, Uri page) =>
        icon.IsAbsoluteUri
        && string.Equals(icon.Scheme, page.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(icon.IdnHost, page.IdnHost, StringComparison.OrdinalIgnoreCase)
        && icon.Port == page.Port;

    static bool IsSvg(Uri icon, string? type) =>
        icon.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type?.Trim(), "image/svg+xml", StringComparison.OrdinalIgnoreCase);
}
