using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Core.Tests.Tasks;

// What keeps the permission desk honest (PermissionKeeper): a question leaves the page, and its slot goes, as soon as
// nobody can answer it there, because the terminal's own dialog is drawn the whole time and may have answered it.
public sealed class PermissionKeeperTests : IDisposable
{
    readonly string box = Path.Combine(Path.GetTempPath(), "deskbar-keeper-" + Guid.NewGuid().ToString("N"));
    readonly TestScheduler scheduler = new();
    readonly Subject<bool> shown = new();
    readonly Subject<IReadOnlyCollection<ClaudeAgent>> lists = new();
    readonly PermissionDesk desk;
    readonly PermissionKeeper keeper;

    // The test scheduler's clock is the desk's and the keeper's clock, from a fixed day.
    static readonly DateTimeOffset Start = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    DateTimeOffset Now() => Start + TimeSpan.FromTicks(scheduler.Clock);

    public PermissionKeeperTests()
    {
        desk = new PermissionDesk(box, Now);
        keeper = new PermissionKeeper(desk, shown, lists, scheduler, Now);
    }

    public void Dispose()
    {
        keeper.Dispose();
        if (Directory.Exists(box)) Directory.Delete(box, true);
    }

    string Asked(string session, string slot)
    {
        Directory.CreateDirectory(Path.Combine(box, slot));
        Assert.True(desk.Register(slot, new PermissionRequest(session, "Bash", "C:\\x", Maybe<string>.None, [])).IsSuccess);
        return slot;
    }

    static ClaudeAgent Agent(string session, string state) =>
        new(session, Maybe<string>.None, @"C:\x", Background: true, Busy: false, Name: "") { State = state };

    void Advance(TimeSpan by) => scheduler.AdvanceBy(by.Ticks);

    [Fact]
    public void Turning_the_setting_or_the_inbox_off_releases_every_question()
    {
        shown.OnNext(true);
        var slot = Asked("s1", "req-1-1");
        Advance(TimeSpan.FromMilliseconds(1));
        Assert.Single(desk.All);

        shown.OnNext(false);
        Advance(TimeSpan.FromMilliseconds(1));

        Assert.Empty(desk.All);
        Assert.False(Directory.Exists(Path.Combine(box, slot)));
    }

    [Fact]
    public void A_list_that_no_longer_shows_the_session_blocked_takes_its_question_off()
    {
        Asked("s1", "req-1-1");
        Asked("s2", "req-2-2");
        Advance(PermissionKeeper.ListLag + TimeSpan.FromSeconds(1));

        lists.OnNext([Agent("s1", "working"), Agent("s2", PermissionKeeper.Blocked)]);
        Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal("s2", desk.All.Single().Request.SessionId);
    }

    // A list read just before a question arrived cannot show it, so a fresh question survives it.
    [Fact]
    public void A_fresh_question_survives_a_list_that_cannot_show_it_yet()
    {
        Asked("s1", "req-1-1");
        Advance(TimeSpan.FromSeconds(1));

        lists.OnNext([Agent("s1", "working")]);
        Advance(TimeSpan.FromMilliseconds(1));

        Assert.Single(desk.All);
    }

    [Fact]
    public void A_question_held_too_long_expires()
    {
        Asked("s1", "req-1-1");

        Advance(PermissionDesk.HeldFor + PermissionKeeper.ExpireEvery);

        Assert.Empty(desk.All);
    }

    [Fact]
    public void Disposing_releases_every_question()
    {
        var slot = Asked("s1", "req-1-1");

        keeper.Dispose();

        Assert.Empty(desk.All);
        Assert.False(Directory.Exists(Path.Combine(box, slot)));
    }

    [Theory]
    [InlineData("2.1.332 (Claude Code)", true)]
    [InlineData("2.2.0 (Claude Code)", true)]
    [InlineData("2.1.331 (Claude Code)", false)]
    [InlineData("not a version", false)]
    public void The_hook_is_written_only_for_the_version_it_was_measured_on_or_newer(string output, bool supported) =>
        Assert.Equal(supported, ClaudeVersion.SupportsPermissionHook(output));
}
