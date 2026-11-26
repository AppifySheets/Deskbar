using System.Net.Http.Headers;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Windows.Shell;

// THE ONLY NETWORK REQUEST A BOOKMARK EVER MAKES, and it is published on deskbar.app/privacy, so what this file
// does must match that page exactly:
//
//   * one plain GET of <origin>/favicon.ico, nothing else (no <link rel="icon"> page parse, no second request);
//   * redirects are NOT followed (a 3xx counts as "no icon": a redirect leaves the origin or lands on a login page);
//   * no cookies, no credentials, user agent "Deskbar";
//   * the answer is at most 256 KB and must say image/*;
//   * certificate errors are accepted on this handler alone (CreateHandler explains why);
//   * program and app bookmarks never reach here, so they make no request.
//
// Nothing here writes to disk. The caller saves an icon only after it decoded it as an image (FaviconStore), so a
// body WPF cannot decode is never stored, and a failure is never stored either: it is simply asked for again at
// the next start.
public sealed class FaviconFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
{
    const int MaxBytes = 262144; // 256 KB
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // The timeout is enforced with a cancellation source below, so HttpClient's own timer is switched off.
    readonly HttpClient client = new(handler ?? CreateHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    readonly TimeSpan limit = timeout ?? DefaultTimeout;

    // The handler the app uses. Nothing is remembered between requests and nothing is followed.
    internal static SocketsHttpHandler CreateHandler() =>
        new()
        {
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            // A redirect leaves the origin or lands on a login page, so it counts as no icon rather than being followed.
            AllowAutoRedirect = false,
            SslOptions =
            {
                // THE ONE PLACE CERTIFICATE CHECKS ARE RELAXED. Home-lab services (Portainer, a router, a NAS) very
                // often serve a self-signed certificate, and their favicon is exactly what a bookmark wants. This is
                // safe enough here because the answer is only ever decoded as an image: it is never run, parsed as
                // HTML or trusted for anything, and no credential or cookie is sent to it.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

    // The icon bytes for the link's origin, or the reason there are none. Never throws for a network problem.
    public async Task<Result<byte[]>> Fetch(Uri link, CancellationToken cancel)
    {
        if (link.Scheme is not ("http" or "https")) return Result.Failure<byte[]>("Only http and https links have a favicon.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(limit);
        try
        {
            // Authority carries host and port only, never user information.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{link.Scheme}://{link.Authority}/favicon.ico");
            request.Headers.TryAddWithoutValidation("User-Agent", "Deskbar");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (!response.IsSuccessStatusCode) return Result.Failure<byte[]>($"The server answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                return Result.Failure<byte[]>("The answer is not an image.");
            if (response.Content.Headers.ContentLength > MaxBytes) return Result.Failure<byte[]>("The icon is larger than 256 KB.");

            // Read one byte past the cap so a body of unknown length that is too big is noticed, not truncated.
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[MaxBytes + 1];
            var count = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
            return count > MaxBytes ? Result.Failure<byte[]>("The icon is larger than 256 KB.")
                : count == 0 ? Result.Failure<byte[]>("The answer is empty.")
                : bytes[..count];
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<byte[]>(cancel.IsCancellationRequested ? "Cancelled." : "The server took longer than the time allowed.");
        }
        catch (Exception e)
        {
            return Result.Failure<byte[]>(e.Message);
        }
    }
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
