using System.IO;
using System.Net;
using System.Net.Http;
using Deskbar.Windows.Shell;

namespace Deskbar.Windows.Tests;

// The favicon fetch and its disk store. NO real network request is made: every test hands the fetcher a fake
// handler, which stands in for the server and records what the fetcher sent. The privacy page on deskbar.app
// promises exactly what these assert, so they are the guard against the code drifting from it.
public sealed class BookmarkFaviconTests : IDisposable
{
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-favicon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    // A server that answers with `respond` and keeps the request it was sent.
    sealed class FakeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen = request;
            return respond(request, cancel);
        }
    }

    // A body with no Content-Length, streaming `size` bytes, so only the read cap can stop it.
    sealed class UnsizedContent(int size) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[size]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    static Task<HttpResponseMessage> Answer(HttpStatusCode status, string? type, HttpContent? content = null) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = content ?? new ByteArrayContent(Png) { Headers = { { "Content-Type", type ?? "image/png" } } },
        });

    static FakeServer Serving(HttpStatusCode status = HttpStatusCode.OK, string? type = "image/png", HttpContent? content = null) =>
        new((_, _) => Answer(status, type, content));

    [Fact]
    public async Task The_request_is_a_plain_get_of_favicon_ico_at_the_origin()
    {
        var server = Serving();
        var result = await new FaviconFetcher(server).Fetch(new Uri("https://portainer.local:9443/deep/page?x=1#f"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(Png, result.Value);
        Assert.Equal(HttpMethod.Get, server.Seen!.Method);
        Assert.Equal("https://portainer.local:9443/favicon.ico", server.Seen.RequestUri!.ToString());
        Assert.Equal("Deskbar", server.Seen.Headers.UserAgent.ToString());
        Assert.False(server.Seen.Headers.Contains("Cookie"));
        Assert.Null(server.Seen.Headers.Authorization);
    }

    [Fact]
    public async Task The_request_never_carries_the_links_user_information()
    {
        var server = Serving();
        await new FaviconFetcher(server).Fetch(new Uri("https://me:secret@portainer.local/"), default);

        Assert.Equal("https://portainer.local/favicon.ico", server.Seen!.RequestUri!.ToString());
        Assert.Null(server.Seen.Headers.Authorization);
    }

    [Fact]
    public void The_handler_sends_no_cookies_or_credentials_and_follows_no_redirect()
    {
        using var handler = FaviconFetcher.CreateHandler();
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.AllowAutoRedirect);
        // The one relaxed certificate check: a self-signed home-lab certificate must not stop the icon.
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback!(this, null, null, default));
    }

    [Fact]
    public async Task A_page_that_is_not_an_image_is_no_icon() =>
        Assert.True((await new FaviconFetcher(Serving(type: "text/html")).Fetch(new Uri("http://a.local/"), default)).IsFailure);

    [Fact]
    public async Task A_missing_icon_is_no_icon() =>
        Assert.True((await new FaviconFetcher(Serving(HttpStatusCode.NotFound)).Fetch(new Uri("http://a.local/"), default)).IsFailure);

    [Fact]
    public async Task A_body_over_256_KB_is_no_icon()
    {
        var content = new UnsizedContent(300 * 1024);
        content.Headers.ContentType = new("image/x-icon");
        Assert.True((await new FaviconFetcher(Serving(content: content)).Fetch(new Uri("http://a.local/"), default)).IsFailure);
    }

    [Fact]
    public async Task A_declared_length_over_256_KB_is_no_icon()
    {
        var content = new ByteArrayContent(new byte[262145]) { Headers = { { "Content-Type", "image/png" } } };
        Assert.True((await new FaviconFetcher(Serving(content: content)).Fetch(new Uri("http://a.local/"), default)).IsFailure);
    }

    [Fact]
    public async Task A_body_of_exactly_256_KB_is_an_icon()
    {
        var content = new UnsizedContent(262144);
        content.Headers.ContentType = new("image/x-icon");
        Assert.True((await new FaviconFetcher(Serving(content: content)).Fetch(new Uri("http://a.local/"), default)).IsSuccess);
    }

    [Fact]
    public async Task A_redirect_is_no_icon()
    {
        var server = new FakeServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/login", UriKind.Relative) } }));
        Assert.True((await new FaviconFetcher(server).Fetch(new Uri("http://a.local/"), default)).IsFailure);
        // Only the one request: the redirect was not chased.
        Assert.Equal("http://a.local/favicon.ico", server.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_slow_server_times_out()
    {
        // The timeout is a constructor parameter (default 5 s) so this need not wait five seconds.
        var server = new FakeServer(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return new HttpResponseMessage();
        });
        var result = await new FaviconFetcher(server, TimeSpan.FromMilliseconds(150)).Fetch(new Uri("http://a.local/"), default);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task A_link_that_is_not_http_makes_no_request()
    {
        var server = Serving();
        Assert.True((await new FaviconFetcher(server).Fetch(new Uri("ftp://a.local/"), default)).IsFailure);
        Assert.Null(server.Seen);
    }

    // THE FALLBACK (FetchDeclared): the bookmarked page, then the icon it declares on its own origin. A server that
    // answers by path and records every request, so the tests can count them (three in all, with /favicon.ico).
    sealed class SiteServer(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request.RequestUri!.PathAndQuery));
        }
    }

    static HttpResponseMessage Page(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    static HttpResponseMessage Picture() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } } };

    [Fact]
    public async Task The_fallback_reads_the_page_then_fetches_its_declared_icon()
    {
        var server = new SiteServer(path => path switch
        {
            "/" => Page("""<html><head><link rel="icon" href="dist/logo.png"></head></html>"""),
            "/dist/logo.png" => Picture(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await new FaviconFetcher(server).FetchDeclared(new Uri("https://me:pw@100.65.0.11:9443/#!/home"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(Png, result.Value);
        Assert.Equal(["https://100.65.0.11:9443/", "https://100.65.0.11:9443/dist/logo.png"],
            server.Seen.Select(r => r.RequestUri!.ToString()));
        Assert.All(server.Seen, r =>
        {
            Assert.Equal("Deskbar", r.Headers.UserAgent.ToString());
            Assert.Null(r.Headers.Authorization);
            Assert.False(r.Headers.Contains("Cookie"));
        });
    }

    [Fact]
    public async Task The_fallback_never_asks_another_site()
    {
        var server = new SiteServer(_ => Page("""<link rel="icon" href="https://cdn.example.com/logo.png">"""));

        var result = await new FaviconFetcher(server).FetchDeclared(new Uri("https://portainer.local/"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(["https://portainer.local/"], server.Seen.Select(r => r.RequestUri!.ToString()));
    }

    [Fact]
    public async Task The_fallback_takes_only_an_html_page()
    {
        var server = new SiteServer(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"icon":"x.png"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        Assert.True((await new FaviconFetcher(server).FetchDeclared(new Uri("https://portainer.local/"), default)).IsFailure);
        Assert.Single(server.Seen);
    }

    [Fact]
    public async Task The_fallback_reads_no_more_than_512_KB_of_the_page()
    {
        // The <link> sits past the cap, so a fetcher that read the whole page would find it.
        var html = "<html><head>" + new string(' ', 600 * 1024) + """<link rel="icon" href="late.png"></head></html>""";
        var server = new SiteServer(path => path == "/" ? Page(html) : Picture());

        Assert.True((await new FaviconFetcher(server).FetchDeclared(new Uri("https://portainer.local/"), default)).IsFailure);
        Assert.Single(server.Seen);
    }

    [Fact]
    public async Task The_fallback_follows_no_redirect_of_the_page()
    {
        var server = new SiteServer(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/login", UriKind.Relative) } });

        Assert.True((await new FaviconFetcher(server).FetchDeclared(new Uri("https://portainer.local/"), default)).IsFailure);
        Assert.Single(server.Seen);
    }

    // P8: this is the store's guarantee, so it lives here and not in the fetcher's tests. The caller saves only a
    // fetched icon, and a failed fetch leaves nothing on disk to be found at the next start.
    [Fact]
    public async Task A_failure_is_not_written_to_the_store()
    {
        var link = new Uri("http://a.local/");
        var store = new FaviconStore(folder);
        var fetched = await new FaviconFetcher(Serving(HttpStatusCode.NotFound)).Fetch(link, default);
        if (fetched.IsSuccess) store.Save(link, fetched.Value);

        Assert.True(fetched.IsFailure);
        Assert.True(store.Cached(link).HasNoValue);
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public void A_saved_icon_comes_back_from_the_store()
    {
        var store = new FaviconStore(folder);
        Assert.True(store.Save(new Uri("https://Portainer.local:9443/a"), Png).IsSuccess);

        Assert.Equal(Png, store.Cached(new Uri("https://portainer.local:9443/other")).Value);
        Assert.Equal(["https_portainer.local_9443.ico"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.True(store.Cached(new Uri("https://portainer.local/")).HasNoValue);
    }
}
