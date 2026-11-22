using System.Net;
using System.Text;
using System.Text.Json;
using Deskbar.Core.Reporting;

namespace Deskbar.Core.Tests.Reporting;

// The app's half of the relay protocol (relay/README.md). The relay itself is tested in relay/test; these
// pin what the app sends and how it reads the answers, against a fake handler standing in for the relay.
public class ReportClientTests
{
    static readonly Uri Relay = new("https://report.deskbar.app/");

    // Records every request and answers each with the next canned response (201 when it runs out).
    sealed class FakeRelay(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (Throw is { } failure) throw failure;
            Requests.Add((request.Method, request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancel)));
            var (status, body) = Requests.Count <= answers.Length ? answers[Requests.Count - 1] : (HttpStatusCode.Created, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    static ProblemReport Report(params ReportAttachment[] attachments) => new(
        "0123456789abcdef0123456789abcdef", "The bar vanished", "me@example.com", "2.5.12+1a2b3c4d5e", "10.0.26200.0", false, true, attachments);

    static ReportClient Client(FakeRelay relay) => new(new HttpClient(relay), Relay);

    [Fact]
    public async Task Each_attachment_goes_up_as_base64_before_the_report()
    {
        var relay = new FakeRelay(
            (HttpStatusCode.Created, "{}"), (HttpStatusCode.Created, "{}"), (HttpStatusCode.Created, """{"number":12}"""));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var dump = Encoding.UTF8.GetBytes("dump text");

        var result = await Client(relay).SendAsync(
            Report(new ReportAttachment("shot-1.png", png), new ReportAttachment("dump.txt", dump)), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Equal(12, result.Value);
        Assert.Equal(3, relay.Requests.Count);
        Assert.Equal("https://report.deskbar.app/v1/attachment?report=0123456789abcdef0123456789abcdef&name=shot-1.png", relay.Requests[0].Url);
        Assert.Equal(Convert.ToBase64String(png), relay.Requests[0].Body);
        Assert.Equal(Convert.ToBase64String(dump), relay.Requests[1].Body);
        Assert.Equal("https://report.deskbar.app/v1/report", relay.Requests[2].Url);
    }

    [Fact]
    public async Task The_report_carries_every_field_the_relay_validates()
    {
        var relay = new FakeRelay((HttpStatusCode.Created, "{}"), (HttpStatusCode.Created, """{"number":3}"""));

        await Client(relay).SendAsync(Report(new ReportAttachment("dump.txt", [1])), null, CancellationToken.None);

        var json = JsonDocument.Parse(relay.Requests[^1].Body).RootElement;
        Assert.Equal("0123456789abcdef0123456789abcdef", json.GetProperty("reportId").GetString());
        Assert.Equal("The bar vanished", json.GetProperty("text").GetString());
        Assert.Equal("me@example.com", json.GetProperty("contact").GetString());
        Assert.Equal("2.5.12+1a2b3c4d5e", json.GetProperty("appVersion").GetString());
        Assert.Equal("10.0.26200.0", json.GetProperty("windowsBuild").GetString());
        Assert.False(json.GetProperty("store").GetBoolean());
        Assert.True(json.GetProperty("pro").GetBoolean());
        Assert.Equal(["dump.txt"], json.GetProperty("attachments").EnumerateArray().Select(e => e.GetString()));
    }

    // Try again after a send that got halfway: the files already up answer 409, which is not a failure.
    [Fact]
    public async Task An_attachment_that_is_already_there_counts_as_uploaded()
    {
        var relay = new FakeRelay((HttpStatusCode.Conflict, """{"error":"attachment already uploaded"}"""), (HttpStatusCode.Created, """{"number":7}"""));

        var result = await Client(relay).SendAsync(Report(new ReportAttachment("shot-1.png", [1])), null, CancellationToken.None);

        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task A_refused_upload_stops_before_the_report_and_says_why()
    {
        var relay = new FakeRelay((HttpStatusCode.TooManyRequests, """{"error":"too many attachments"}"""));

        var result = await Client(relay).SendAsync(
            Report(new ReportAttachment("shot-1.png", [1]), new ReportAttachment("dump.txt", [2])), null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Single(relay.Requests);
        Assert.Equal("Too many reports from this computer in the last minute. Try again in a minute.", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "A file is too large for the report.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "report.deskbar.app is not ready to take reports yet.")]
    [InlineData(HttpStatusCode.BadGateway, "report.deskbar.app refused the report (HTTP 502: github said no).")]
    public async Task Refusals_are_explained_in_words(HttpStatusCode status, string expected)
    {
        var relay = new FakeRelay((status, """{"error":"github said no"}"""));

        var result = await Client(relay).SendAsync(Report(), null, CancellationToken.None);

        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task No_connection_is_said_plainly()
    {
        var relay = new FakeRelay { Throw = new HttpRequestException("No such host is known.") };

        var result = await Client(relay).SendAsync(Report(), null, CancellationToken.None);

        Assert.Equal("No connection to report.deskbar.app.", result.Error);
    }

    [Fact]
    public async Task A_timeout_is_not_reported_as_a_cancel()
    {
        var relay = new FakeRelay { Throw = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") };

        var result = await Client(relay).SendAsync(Report(), null, CancellationToken.None);

        Assert.Equal("report.deskbar.app did not answer in time.", result.Error);
    }

    [Fact]
    public async Task Cancelling_says_so()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var relay = new FakeRelay { Throw = new TaskCanceledException() };

        var result = await Client(relay).SendAsync(Report(), null, cancel.Token);

        Assert.Equal("Sending was cancelled.", result.Error);
    }

    [Fact]
    public async Task Progress_counts_every_file_and_then_the_report()
    {
        var relay = new FakeRelay();
        var seen = new List<ReportProgress>();

        await Client(relay).SendAsync(
            Report(new ReportAttachment("shot-1.png", [1]), new ReportAttachment("shot-2.jpg", [2]), new ReportAttachment("dump.txt", [3])),
            new SyncProgress(seen.Add), CancellationToken.None);

        Assert.Equal(
            ["1/4 screenshot 1", "2/4 screenshot 2", "3/4 the debug dump", "4/4 the report"],
            seen.Select(p => $"{p.Step}/{p.Steps} {p.What}"));
    }

    // Progress<T> posts to a captured context; this one reports inline so the test sees every step in order.
    sealed class SyncProgress(Action<ReportProgress> report) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress value) => report(value);
    }

    [Fact]
    public async Task An_unreadable_answer_to_the_report_is_a_failure()
    {
        var relay = new FakeRelay((HttpStatusCode.Created, "not json"));

        var result = await Client(relay).SendAsync(Report(), null, CancellationToken.None);

        Assert.Equal("report.deskbar.app accepted the report but its answer could not be read.", result.Error);
    }
}
