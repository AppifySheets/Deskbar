using CSharpFunctionalExtensions;

namespace Deskbar.Core.Updates;

// The update check's second way in, for when GitHub's API says no (Deskbar task 49b68e43).
//
// WHY IT EXISTS. The check asks api.github.com for the latest release, and the API allows sixty
// unauthenticated requests an hour PER IP ADDRESS. Every copy of Deskbar asks every five minutes, twelve
// an hour, so on a corporate network, where a whole office shares one public address, a handful of copies
// (plus anything else on that network calling the API without a token) use the hour's budget up and the
// API answers 403 until it resets. Measured on Petre's own machine, alone on its address: 13 of the 60
// used within the hour. That is why on a corporate computer the background check still found the update
// (some five-minute tick lands just after a reset) while "Check for updates" asked by hand got
// "release check returned 403".
//
// WHAT IT DOES INSTEAD. Two plain github.com requests, neither of which counts against the API's limit:
//
//   1. HEAD https://github.com/<repo>/releases/latest answers 302 with Location .../releases/tag/v2.6.48.
//      The tag is the version. (A repository with no release redirects to .../releases, no tag, and
//      that reads as a failure, never as "you are up to date".)
//   2. HEAD https://github.com/<repo>/releases/download/v2.6.48/Deskbar-2.6.48-win-x64.exe answers 302
//      to the file's storage once the release build has attached the exe, and 404 before that. That is
//      the API's "assets" list reduced to the one question the app asks of it (UpdateCheck.CanDownload):
//      is the portable exe there yet.
//
// The asset's NAME is predicted rather than listed, which is the price of not using the API. It is the
// name release.yml gives the exe ("Name the executable after its version": Deskbar-<version>-win-x64.exe,
// with the tag being v<version>), and this repository owns both ends, so the prediction is a contract
// between two files of the same repo rather than a guess about someone else's. If the workflow ever
// renames the exe, this fallback reads every release as "exe not attached yet" (quiet, never a wrong
// download), and the API path, which lists the real assets, keeps working whenever it is not limited.
//
// The API stays the FIRST choice (UpdateService.NewerThanRunningAsync): it is the documented contract and
// lists the real assets. This class only runs when the API call failed.
//
// Everything here decides and nothing here touches the network, same split as UpdateCheck: the HTTP calls
// in Deskbar.App.UpdateService fetch the two answers and hand them over.
public static class WebFallback
{
    // The release tag out of the Location header of GET/HEAD <repository>/releases/latest.
    //
    // Strict on purpose: the tag becomes part of a download URL and of a file name next to the running exe
    // (via PortableAssetName), so anything that is not exactly "<repository>/releases/tag/<a version>" is a
    // failure. The redirect is from github.com, but the rule in UpdateCheck holds here too: anything this
    // cannot understand means no update.
    public static Result<string> TagFrom(string repository, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return Result.Failure<string>("releases/latest did not redirect");

        var prefix = repository.TrimEnd('/') + "/releases/tag/";

        // A repository with no published release redirects to the release LIST instead; saying so plainly
        // beats "not a version", which would send someone looking in the wrong place.
        if (!location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<string>($"releases/latest redirected somewhere unexpected: {location}");

        var tag = Uri.UnescapeDataString(location[prefix.Length..]);

        // A bare version and nothing else: no further path, no query, nothing a file name cannot carry.
        return UpdateCheck.IsBareFileName(tag) && !tag.Contains('/') && !tag.Contains('?') && !tag.Contains('#')
               && UpdateCheck.IsVersion(tag)
            ? tag
            : Result.Failure<string>($"releases/latest named a tag that is not a version: {tag}");
    }

    // The portable exe's file name for a tag: "v2.6.48" -> "Deskbar-2.6.48-win-x64.exe". Mirrors the
    // "Name the executable after its version" step of .github/workflows/release.yml; change both together.
    //
    // Always the PORTABLE exe, which is right while every caller is the portable build (UpdateService passes
    // preferSetup: false to the API path). An installed build that wants the installer would need its own
    // name here, or it would be handed the portable exe whenever the API is rate-limited.
    public static string PortableAssetName(string tag) =>
        $"Deskbar-{(tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag)}-win-x64.exe";

    // Where that exe would be downloaded from. The same URL shape the API reports as browser_download_url,
    // so UpdateCheck.IsDownloadable accepts it and the downloader treats it exactly like the API's.
    public static string DownloadUrl(string repository, string tag) =>
        $"{repository.TrimEnd('/')}/releases/download/{Uri.EscapeDataString(tag)}/{PortableAssetName(tag)}";

    // What a HEAD of DownloadUrl said, as one of three answers.
    //
    // Attached: a redirect to GitHub's file storage, which is the only way github.com serves a release asset
    // (measured: 302 to release-assets.githubusercontent.com). The redirect's TARGET is checked, not just its
    // status, because an office web filter that blocks .exe downloads typically answers with a 200 block page
    // or a redirect to its own block page; read as "attached", that would offer an update and then download
    // the block page's HTML as the new exe.
    //
    // Not attached yet: 404, the routine two or three minutes after publishing (UpdateCheck.CanDownload).
    //
    // Anything else is a failed check, not a "no", so it is retried on the next tick instead of being read
    // as an answer.
    public static Result<bool> AssetPublished(int statusCode, string? location) =>
        statusCode switch
        {
            301 or 302 or 303 or 307 or 308 when UpdateCheck.IsDownloadable(location) => true,
            404 => false,
            _ => Result.Failure<bool>($"asset check returned {statusCode}{(location is null ? "" : $" to {location}")}"),
        };

    // The release the two answers describe, in the same shape ReadRelease gives from the API, so nothing
    // downstream can tell which way the answer came. No exe yet means no asset, which CanDownload reads as
    // "not yet news", exactly as for an API payload with an empty assets list.
    public static ReleaseInfo Release(string repository, string tag, bool assetPublished) =>
        new(tag,
            $"{repository.TrimEnd('/')}/releases/tag/{Uri.EscapeDataString(tag)}",
            assetPublished ? PortableAssetName(tag) : null,
            assetPublished ? DownloadUrl(repository, tag) : null);
}
