using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Where a terminal or VS Code session's transcript stood when it asked a permission question, and whether it has moved on
// (TranscriptMark, task 6306d261). The lines used are the "type" values measured on 2026-11-27: during the wait only an
// ai-title line arrived; after the answer file-history-snapshot, tools-used, assistant and user lines.
public sealed class TranscriptMarkTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-mark-" + Guid.NewGuid().ToString("N"));
    readonly string path;

    public TranscriptMarkTests()
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "session.jsonl");
        File.WriteAllText(path, "{\"type\":\"user\",\"message\":\"run mkdir\"}\n{\"type\":\"assistant\"}\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    TranscriptMark Mark() => TranscriptMark.Take(path).Value;

    [Fact]
    public void A_mark_is_the_transcripts_length_and_none_without_a_transcript()
    {
        Assert.Equal(new FileInfo(path).Length, Mark().Length);
        Assert.True(TranscriptMark.Take(Maybe<string>.None).HasNoValue);
        Assert.True(TranscriptMark.Take(Path.Combine(folder, "missing.jsonl")).HasNoValue);
    }

    // A subagent's question is answered in the subagent's own file beside the session's (the layout on disk, 2026-11-27).
    [Fact]
    public void A_subagents_transcript_sits_beside_the_sessions()
    {
        var session = Path.Combine(folder, "3ebd4031.jsonl");

        Assert.Equal(session, TranscriptMark.For(session, Maybe<string>.None).Value);
        Assert.Equal(Path.Combine(folder, "3ebd4031", "subagents", "agent-ab0c.jsonl"), TranscriptMark.For(session, "ab0c").Value);
        Assert.True(TranscriptMark.For(Maybe<string>.None, "ab0c").HasNoValue);
    }

    [Fact]
    public void Nothing_written_is_not_moving_on()
    {
        Assert.False(Mark().MovedOn());
    }

    // Bookkeeping lines say nothing about the question: a title was measured arriving during a wait.
    [Fact]
    public void Bookkeeping_lines_are_not_moving_on()
    {
        var mark = Mark();
        File.AppendAllText(path, "{\"type\":\"ai-title\",\"title\":\"x\"}\n{\"type\":\"last-prompt\"}\n{\"type\":\"attachment\"}\n");

        Assert.False(mark.MovedOn());
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("user")]
    public void A_conversation_line_past_the_mark_is_moving_on(string type)
    {
        var mark = Mark();
        File.AppendAllText(path, "{\"type\":\"file-history-snapshot\"}\n{\"type\":\"tools-used\"}\n{\"type\":\"" + type + "\"}\n");

        Assert.True(mark.MovedOn());
    }

    // A line Claude Code is still writing (no newline yet) is left for the next look.
    [Fact]
    public void A_half_written_line_waits_for_its_newline()
    {
        var mark = Mark();
        File.AppendAllText(path, "{\"type\":\"assist");
        Assert.False(mark.MovedOn());

        File.AppendAllText(path, "ant\"}\n");
        Assert.True(mark.MovedOn());
    }

    // Lines that are not JSON, or JSON without a type, are skipped rather than taken as moving on.
    [Fact]
    public void Lines_that_do_not_read_are_skipped()
    {
        var mark = Mark();
        File.AppendAllText(path, "not json\n[1,2]\n{\"kind\":\"assistant\"}\n");

        Assert.False(mark.MovedOn());
    }

    // A transcript that is gone or cut short is not the one that asked: the question is settled.
    [Fact]
    public void A_gone_or_shorter_transcript_is_moving_on()
    {
        var mark = Mark();
        File.WriteAllText(path, "{}\n");
        Assert.True(mark.MovedOn());

        File.Delete(path);
        Assert.True(mark.MovedOn());
    }
}
