using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// The name a Claude Code session's tab shows, read from the end of its transcript (SessionTitle). The entry shapes are
// the ones in the transcripts on Petre's machine on 2026-11-23: custom-title after a rename, ai-title for a generated
// name, both appended again as the session goes on.
public class SessionTitleTests
{
    static string Line(string type, string field, string name) =>
        $$"""{"type":"{{type}}","{{field}}":"{{name}}","sessionId":"s1"}""";

    static string Custom(string name) => Line("custom-title", "customTitle", name);
    static string Generated(string name) => Line("ai-title", "aiTitle", name);
    const string Message = """{"type":"assistant","message":{"content":[{"type":"text","text":"hello"}]}}""";

    [Fact]
    public void A_generated_name_is_the_name_when_there_is_no_custom_one() =>
        Assert.Equal("Pinned window flies", SessionTitle.Last(string.Join('\n', Message, Generated("Pinned window flies"), Message)).GetValueOrDefault());

    // A rename is what the tab shows, even when a generated name was written after it.
    [Fact]
    public void A_custom_name_wins_over_a_later_generated_one() =>
        Assert.Equal("Deskbar: fix flaky tests",
            SessionTitle.Last(string.Join('\n', Custom("Deskbar: fix flaky tests"), Generated("Flaky tests"), Message)).GetValueOrDefault());

    [Fact]
    public void The_last_rename_wins() =>
        Assert.Equal("second", SessionTitle.Last(string.Join('\n', Custom("first"), Message, Custom("second"))).GetValueOrDefault());

    // A window into the middle of the file cuts its first line in half; that half is dropped, not misread.
    [Fact]
    public void A_cut_first_line_is_dropped() =>
        Assert.Equal(Maybe<string>.None, SessionTitle.Last("tomTitle\":\"half\"}\n" + Message, cutFirstLine: true));

    [Fact]
    public void A_transcript_with_no_title_has_no_name() =>
        Assert.Equal(Maybe<string>.None, SessionTitle.Last(string.Join('\n', Message, Message)));

    // A message that merely MENTIONS a title entry's text is not one.
    [Fact]
    public void A_message_quoting_a_title_entry_is_not_one() =>
        Assert.Equal(Maybe<string>.None, SessionTitle.Last("""{"type":"user","message":"the \"custom-title\" entry"}"""));

    [Fact]
    public void A_missing_file_or_no_path_has_no_name()
    {
        Assert.Equal(Maybe<string>.None, SessionTitle.Read(null));
        Assert.Equal(Maybe<string>.None, SessionTitle.Read(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.jsonl")));
    }

    // From a real file: the title sits near the end, behind more than the first 64 KB window of messages from the
    // start, and the file is read while still open for writing, as Claude Code holds it.
    [Fact]
    public void The_name_is_read_from_the_end_of_a_transcript_still_being_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskbar-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            var text = string.Join('\n', [Custom("old name"), .. Enumerable.Repeat(Message, 2000), Custom("Deskbar: fix flaky tests"), Message]) + "\n";
            writer.Write(Encoding.UTF8.GetBytes(text));
            writer.Flush();

            Assert.Equal("Deskbar: fix flaky tests", SessionTitle.Read(path).GetValueOrDefault());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A title further back than the first window is still found by a wider one.
    [Fact]
    public void A_title_further_back_than_the_first_window_is_found()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskbar-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, string.Join('\n', [Custom("far back"), .. Enumerable.Repeat(Message, 3000)]) + "\n");
            Assert.True(new FileInfo(path).Length > 64 * 1024);

            Assert.Equal("far back", SessionTitle.Read(path).GetValueOrDefault());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
