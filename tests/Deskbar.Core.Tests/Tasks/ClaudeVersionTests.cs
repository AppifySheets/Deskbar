using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Whether the Claude Code on this machine honours "asyncRewake", the hook option the comment waiter needs (final review
// of the comment-wakes-session branch, I6). A build that ignored it would run the waiter as an ordinary Stop hook and
// hold its session after every turn, so the waiter's entry is written only for a version known to have it: 2.1.322, the
// oldest build measured with it on 2026-11-24 (also 2.1.330, which the VS Code sessions here run, and 2.1.331).
public class ClaudeVersionTests
{
    [Theory]
    [InlineData("2.1.331 (Claude Code)", true)]
    [InlineData("2.1.322 (Claude Code)", true)]
    [InlineData("2.2.0 (Claude Code)", true)]
    [InlineData("3.0.1", true)]
    [InlineData("2.1.321 (Claude Code)", false)]
    [InlineData("1.0.80 (Claude Code)", false)]
    [InlineData("", false)]
    [InlineData("claude: command not found", false)]
    public void Only_a_build_known_to_have_async_rewake_supports_the_waiter(string versionOutput, bool supports) =>
        Assert.Equal(supports, ClaudeVersion.SupportsAsyncRewake(versionOutput));
}
