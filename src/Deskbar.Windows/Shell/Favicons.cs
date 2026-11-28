using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Windows.Shell;

// THE ONLY NETWORK REQUESTS A BOOKMARK EVER MAKES, and they are published on deskbar.app/privacy, so what this file
// does must match that page exactly:
//
//   * first one plain GET of <origin>/favicon.ico (Fetch);
//   * only when that gives no icon the caller can decode, the fallback (FetchDeclared, decided with Petre on 2026-11-26
//     for Portainer, which answers 404 there and names its logo in the page): one GET of the bookmarked page itself,
//     without its #fragment, read as text/html only and at most 512 KB of it; then the icon that page declares in a
//     <link rel="...icon..."> (DeclaredIcon, in Core), fetched only when it is on the SAME scheme, host and port and is
//     not an svg;
//   * ONE redirect is followed, and only of that page, and only to the same scheme, host and port
//     (DeclaredIcon.RedirectOf, added 2026-11-28 for Headplane, whose deep links send a cookie-less request to its
//     login page, the page that names its logo). So four requests at most for one origin, and never one to another
//     site;
//   * every other redirect is NOT followed (a 3xx counts as "no icon"): not of /favicon.ico, not of the declared icon,
//     not a second one of the page, and never one that leaves the origin;
//   * no cookies, no credentials, user agent "Deskbar", and never the link's user:password;
//   * an icon is at most 256 KB and must say image/*;
//   * each request has its own 5 second limit;
//   * certificate errors are accepted on this handler alone (CreateHandler explains why);
//   * program and app bookmarks never reach here, so they make no request.
//
// Nothing here writes to disk. The caller saves an icon only after it decoded it as an image (FaviconStore), keyed by
// origin under %APPDATA%\Deskbar\bookmark-icons, so a body WPF cannot decode is never stored, and a failure is never
// stored either: it is simply asked for again at the next start.
public sealed class FaviconFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
{
    const int MaxBytes = 262144; // 256 KB, for an icon
    const int MaxPageBytes = 524288; // 512 KB of a page, which holds its <head> many times over
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // The timeout is enforced with a cancellation source per request below, so HttpClient's own timer is switched off.
    readonly HttpClient client = new(handler ?? CreateHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    readonly TimeSpan limit = timeout ?? DefaultTimeout;

    // The handler the app uses. Nothing is remembered between requests and nothing is followed.
    internal static SocketsHttpHandler CreateHandler() =>
        new()
        {
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            // The handler follows nothing by itself. The one redirect the fallback takes (a page sending to its login
            // page, on the same origin) is decided and asked for by FetchDeclared, never by the handler.
            AllowAutoRedirect = false,
            // A response read only up to its cap must not go on being read off the wire after it is disposed: by
            // default the handler drains up to 1 MB of what is left to reuse the connection, which would break the
            // cap the privacy page states. Nothing is drained; the connection is simply closed.
            MaxResponseDrainSize = 0,
            SslOptions =
            {
                // THE ONE PLACE CERTIFICATE CHECKS ARE RELAXED. Home-lab services (Portainer, a router, a NAS) very
                // often serve a self-signed certificate, and their favicon is exactly what a bookmark wants. This is
                // safe enough here because what comes back is only ever decoded as an image, or, for the fallback's
                // page, scanned for one <link> tag: it is never run, rendered or trusted for anything, and no
                // credential or cookie is sent to it.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

    // The icon bytes at <origin>/favicon.ico, or the reason there are none. Never throws for a network problem.
    public Task<Result<byte[]>> Fetch(Uri link, CancellationToken cancel) =>
        link.Scheme is not ("http" or "https")
            ? Task.FromResult(Result.Failure<byte[]>("Only http and https links have a favicon."))
            // Authority carries host and port only, never user information.
            : Image(new Uri($"{link.Scheme}://{link.Authority}/favicon.ico"), cancel);

    // THE FALLBACK, asked by the caller only after Fetch gave nothing it could decode: the bookmarked page (or the one
    // same-origin page it redirects to), then the icon it declares on its own origin. Three requests at most; the
    // icon's only when the page names an acceptable one.
    public async Task<Result<byte[]>> FetchDeclared(Uri link, CancellationToken cancel)
    {
        if (link.Scheme is not ("http" or "https")) return Result.Failure<byte[]>("Only http and https links have a favicon.");
        var asked = DeclaredIcon.PageOf(link);
        var first = await Html(asked, cancel);
        // A redirect to the same origin (the login page of an app behind a sign-in) is read instead, once. Its own
        // redirect, if any, is not followed: that second answer is taken as it comes.
        var moved = first.IsFailure ? DeclaredIcon.RedirectOf(asked, first.Error.Status, first.Error.Location) : Maybe<Uri>.None;
        var (page, html) = moved.HasValue ? (moved.Value, await Html(moved.Value, cancel)) : (asked, first);
        if (html.IsFailure) return Result.Failure<byte[]>(html.Error.Reason);
        // Icons are resolved against the page that was actually read, so the login page's relative href lands right.
        var icon = DeclaredIcon.Of(Encoding.UTF8.GetString(html.Value), page);
        return icon.HasNoValue
            ? Result.Failure<byte[]>("The page declares no icon on its own site that Deskbar can show.")
            // The page naming /favicon.ico itself, which is what already failed: asking again would only repeat it.
            : string.Equals(icon.Value.AbsolutePath, "/favicon.ico", StringComparison.OrdinalIgnoreCase) && icon.Value.Query.Length == 0
                ? Result.Failure<byte[]>("The page declares the /favicon.ico that already failed.")
                : await Image(icon.Value, cancel);
    }

    // An icon: a redirect is a failure like any other, so only its reason is kept.
    async Task<Result<byte[]>> Image(Uri url, CancellationToken cancel) =>
        await Get(url, type => type.StartsWith("image/", StringComparison.OrdinalIgnoreCase), "an image", MaxBytes, overflowFails: true, cancel)
            is var image && image.IsSuccess
            ? image.Value
            : Result.Failure<byte[]>(image.Error.Reason);

    // A page, read as text/html only and no further than its cap. A refusal keeps its status and Location, so
    // FetchDeclared can decide whether it is the one redirect it follows.
    Task<Result<byte[], Refusal>> Html(Uri url, CancellationToken cancel) =>
        Get(url, type => type.Equals("text/html", StringComparison.OrdinalIgnoreCase), "a web page", MaxPageBytes, overflowFails: false, cancel);

    // Why a GET gave nothing usable: the reason, for the log, and for an answer that was not a success its status and
    // Location header (0 and null when no answer came, a timeout or a network error). Get never follows the Location
    // itself; only FetchDeclared does, once, for the page.
    sealed record Refusal(string Reason, int Status = 0, Uri? Location = null);

    // One GET with every rule above: the Deskbar user agent and nothing else, no redirect, a media type `typeOk`
    // accepts, at most `max` bytes, within `limit`. `overflowFails`: an icon past its cap is refused whole (a cut icon
    // is a broken one), while a page is simply read no further than its cap, since its <head> comes first.
    async Task<Result<byte[], Refusal>> Get(Uri url, Func<string, bool> typeOk, string wanted, int max, bool overflowFails, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(limit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Deskbar");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            // A redirect keeps its Location, for FetchDeclared to judge; nothing here follows it.
            if (!response.IsSuccessStatusCode)
                return No($"The server answered {(int)response.StatusCode}.", (int)response.StatusCode, response.Headers.Location);
            if (response.Content.Headers.ContentType?.MediaType is not { } type || !typeOk(type))
                return No($"The answer is not {wanted}.");
            if (overflowFails && response.Content.Headers.ContentLength > max)
                return No($"The answer is larger than {max / 1024} KB.");

            // Read one byte past the cap so a body of unknown length that is too big is noticed, not truncated.
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[max + 1];
            var count = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
            return count > max && overflowFails ? No($"The answer is larger than {max / 1024} KB.")
                : count == 0 ? No("The answer is empty.")
                : Result.Success<byte[], Refusal>(bytes[..Math.Min(count, max)]);
        }
        catch (OperationCanceledException)
        {
            return No(cancel.IsCancellationRequested ? "Cancelled." : "The server took longer than the time allowed.");
        }
        catch (Exception e)
        {
            return No(e.Message);
        }
    }

    static Result<byte[], Refusal> No(string reason, int status = 0, Uri? location = null) =>
        Result.Failure<byte[], Refusal>(new Refusal(reason, status, location));
}

// The icons that were fetched and decoded, one file per origin under %APPDATA%\Deskbar\bookmark-icons (the caller
// passes the folder, built from AppPaths.NewStateDir so DESKBAR_APPDATA is honoured). An icon, once stored, is never
// asked for again.
public sealed class FaviconStore(string folder)
{
    string PathOf(Uri link) => Path.Combine(folder, FaviconKey.Of(link) + ".ico");

    public Maybe<byte[]> Cached(Uri link)
    {
        try
        {
            var path = PathOf(link);
            return File.Exists(path) ? File.ReadAllBytes(path) : Maybe<byte[]>.None;
        }
        catch
        {
            return Maybe<byte[]>.None;
        }
    }

    // Through a temp file and a rename, so a crash mid-write never leaves half an icon that Cached would return.
    public Result Save(Uri link, byte[] image) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(folder);
            var path = PathOf(link);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, image);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }
        }, e => $"Could not store the icon: {e.Message}");
}
