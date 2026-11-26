using Deskbar.Core.Updates;

namespace Deskbar.Core.Tests.Updates;

// Deskbar task 49b68e43: on a corporate network GitHub's API rate limit (sixty an hour per IP) is used up
// by the office, so the update check falls back to github.com's own releases/latest redirect and the
// download URL. These pin what that fallback decides from the two answers, without a network.
public class WebFallbackTests
{
    const string Repo = "https://github.com/AppifySheets/Deskbar";

    [Theory]
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.48", "v2.6.48")]
    // The host and owner are compared case-insensitively, as GitHub treats them.
    [InlineData("https://github.com/appifysheets/deskbar/releases/tag/v2.6.48", "v2.6.48")]
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/2.7.0", "2.7.0")]
    public void The_redirect_names_the_tag(string location, string tag) =>
        Assert.Equal(tag, WebFallback.TagFrom(Repo, location).Value);

    [Theory]
    // No redirect at all.
    [InlineData(null)]
    [InlineData("")]
    // A repository with no published release redirects to the list, which is not "up to date".
    [InlineData("https://github.com/AppifySheets/Deskbar/releases")]
    // Another repository or another host is never trusted to name our version.
    [InlineData("https://github.com/someone/else/releases/tag/v9.9.9")]
    [InlineData("https://example.invalid/AppifySheets/Deskbar/releases/tag/v9.9.9")]
    // The tag ends up in a URL and a file name beside the exe: only a bare version gets through.
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/v9.9.9/extra")]
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/..%2F..%2Fv9.9.9")]
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/latest")]
    [InlineData("https://github.com/AppifySheets/Deskbar/releases/tag/")]
    public void Anything_but_a_version_tag_is_a_failure(string? location) =>
        Assert.True(WebFallback.TagFrom(Repo, location).IsFailure);

    // Mirrors release.yml's "Name the executable after its version" step.
    [Fact]
    public void The_asset_name_follows_the_release_workflow() =>
        Assert.Equal("Deskbar-2.6.48-win-x64.exe", WebFallback.PortableAssetName("v2.6.48"));

    [Fact]
    public void The_download_url_is_one_the_downloader_accepts()
    {
        var url = WebFallback.DownloadUrl(Repo, "v2.6.48");

        Assert.Equal("https://github.com/AppifySheets/Deskbar/releases/download/v2.6.48/Deskbar-2.6.48-win-x64.exe", url);
        Assert.True(UpdateCheck.IsDownloadable(url));
    }

    // github.com serves an attached asset as a redirect to its file storage (measured 2026-11-26).
    [Fact]
    public void A_redirect_to_github_storage_means_attached() =>
        Assert.True(WebFallback.AssetPublished(302,
            "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sp=r").Value);

    [Fact]
    public void Not_found_means_not_attached_yet() =>
        Assert.False(WebFallback.AssetPublished(404, null).Value);

    [Theory]
    // A rate limit or a server error is a failed check, retried on the next tick, never read as an answer.
    [InlineData(403, null)]
    [InlineData(429, null)]
    [InlineData(500, null)]
    // An office web filter answering for the exe: its block page, or a redirect to it. Read as "attached",
    // either would download HTML as the new exe.
    [InlineData(200, null)]
    [InlineData(302, "https://filter.corp.invalid/blocked?url=x")]
    [InlineData(302, "http://release-assets.githubusercontent.com/x")]
    [InlineData(302, null)]
    public void Anything_else_is_a_failure(int status, string? location) =>
        Assert.True(WebFallback.AssetPublished(status, location).IsFailure);

    [Fact]
    public void A_release_with_its_exe_can_be_downloaded()
    {
        var release = WebFallback.Release(Repo, "v2.6.48", assetPublished: true);

        Assert.True(UpdateCheck.CanDownload(release));
        Assert.Equal("https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.48", release.PageUrl);
        Assert.True(UpdateCheck.IsNewer("2.6.47+dca791a44f", release.Version));
    }

    // Two or three minutes after publishing the exe is not attached yet: not news, the same as an API
    // payload with no assets.
    [Fact]
    public void A_release_without_its_exe_is_not_yet_news() =>
        Assert.False(UpdateCheck.CanDownload(WebFallback.Release(Repo, "v2.6.48", assetPublished: false)));
}
