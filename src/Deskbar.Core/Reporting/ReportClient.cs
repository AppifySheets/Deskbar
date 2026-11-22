using System.Net;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Reporting;

// Sends a ProblemReport to the relay (relay/README.md has the protocol):
//   1. each attachment as its own POST /v1/attachment?report=<id>&name=<name>, the body the file's base64 text;
//   2. then POST /v1/report with the text, the contact line and the attachment names.
// The relay answers the issue number, which the window shows as the report's reference.
//
// WHY ONE FILE PER REQUEST, ENCODED HERE: the relay runs on Cloudflare's free plan, which allows a Worker
// 10 ms of CPU per request. Base64 in C# costs nothing, and the relay then passes the string through to
// GitHub without decoding it.
//
// A 409 for an attachment means it is already there: the user pressed Try again after a send that got
// part of the way. That counts as uploaded, so a retry never fails on its own earlier success.
//
// The HttpClient is handed in, with its User-Agent ("Deskbar/<version>", which the relay requires) and
// timeout already set, so the tests can put a fake handler under it.
public sealed class ReportClient(HttpClient http, Uri relay)
{
    public async Task<Result<int>> SendAsync(ProblemReport report, IProgress<ReportProgress>? progress, CancellationToken cancel)
    {
        var steps = report.Attachments.Count + 1;
        try
        {
            // The uploads in order, stopping at the first failure. Aggregate over tasks keeps them sequential:
            // a slow line should carry one file at a time, and the progress line counts them one by one.
            var uploaded = await report.Attachments
                .Select((attachment, index) => (attachment, index))
                .Aggregate(Task.FromResult(Result.Success()), async (previous, next) =>
                {
                    var sofar = await previous;
                    if (sofar.IsFailure) return sofar;
                    progress?.Report(new ReportProgress(next.index + 1, steps, Describe(next.attachment.Name)));
                    return await UploadAsync(report.Id, next.attachment, cancel);
                });
            if (uploaded.IsFailure) return Result.Failure<int>(uploaded.Error);

            progress?.Report(new ReportProgress(steps, steps, "the report"));
            return await PostReportAsync(report, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return Result.Failure<int>("Sending was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout surfaces as a cancellation that nobody asked for.
            return Result.Failure<int>($"{relay.Host} did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return Result.Failure<int>($"No connection to {relay.Host}.");
        }
    }

    // What the progress line calls each file.
    static string Describe(string name) =>
        name == ReportLimits.DumpName ? "the debug dump"
        : name.StartsWith("shot-", StringComparison.Ordinal) ? $"screenshot {name[5]}"
        : name;

    async Task<Result> UploadAsync(string id, ReportAttachment attachment, CancellationToken cancel)
    {
        var url = new Uri(relay, $"v1/attachment?report={id}&name={Uri.EscapeDataString(attachment.Name)}");
        using var body = new StringContent(Convert.ToBase64String(attachment.Bytes), Encoding.ASCII, "text/plain");
        using var response = await http.PostAsync(url, body, cancel);
        return response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict
            ? Result.Success()
            : Result.Failure(await ExplainAsync(response, cancel));
    }

    async Task<Result<int>> PostReportAsync(ProblemReport report, CancellationToken cancel)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["reportId"] = report.Id,
            ["text"] = report.Text,
            ["contact"] = report.Contact,
            ["appVersion"] = report.AppVersion,
            ["windowsBuild"] = report.WindowsBuild,
            ["store"] = report.Store,
            ["pro"] = report.Pro,
            ["attachments"] = report.Attachments.Select(a => a.Name).ToArray(),
        });
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(relay, "v1/report"), body, cancel);
        if (response.StatusCode != HttpStatusCode.Created)
            return Result.Failure<int>(await ExplainAsync(response, cancel));

        var answer = await response.Content.ReadAsStringAsync(cancel);
        return Result.Try(() => JsonDocument.Parse(answer).RootElement.GetProperty("number").GetInt32(),
            _ => $"{relay.Host} accepted the report but its answer could not be read.");
    }

    // A refusal in words a person can act on. The relay's own reason ({"error": "..."}) is added where it
    // helps someone reading the saved report later; the status decides the sentence.
    async Task<string> ExplainAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var reason = await ReasonAsync(response, cancel);
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "Too many reports from this computer in the last minute. Try again in a minute.",
            HttpStatusCode.RequestEntityTooLarge => "A file is too large for the report.",
            HttpStatusCode.ServiceUnavailable => $"{relay.Host} is not ready to take reports yet.",
            _ => $"{relay.Host} refused the report (HTTP {(int)response.StatusCode}{(reason is null ? "" : $": {reason}")}).",
        };
    }

    static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var text = await response.Content.ReadAsStringAsync(cancel);
        return Result.Try(() => JsonDocument.Parse(text).RootElement.GetProperty("error").GetString())
            .GetValueOrDefault((string?)null);
    }
}
