using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The poll of a page's transcript on its own (InboxTranscript): when it reads, what it reads, when it stops. A fake timer
// that runs out on demand, a reader that records its calls, and a runner that does the read at once. No disk, no thread.
public class InboxTranscriptTests
{
    sealed class Rig
    {
        sealed class Handle(Rig rig) : IDisposable
        {
            public bool Stopped { get; private set; }

            public void Dispose()
            {
                Stopped = true;
                rig.Waiting.Remove(this);
            }
        }

        public readonly List<object> Waiting = [];
        readonly List<(Action Then, Handle Handle)> timers = [];
        public ChatSource Source = new(ChatSourceKind.Live, @"C:\made\up\a.jsonl", "a");
        public readonly List<string> Read = [];
        public int Changed;
        public bool Deferred;
        public readonly List<Action> Pending = [];
        public ChatUpdate Answer = new([], true, 10);
        public InboxTranscript Poll { get; }

        public Rig()
        {
            Poll = new InboxTranscript(_ => Source, () => path =>
            {
                Read.Add(path);
                return Answer;
            }, (work, done) =>
            {
                if (Deferred) Pending.Add(() => done(work()));
                else done(work());
            }, (after, then) =>
            {
                Assert.Equal(InboxTranscript.Every, after);
                var handle = new Handle(this);
                timers.Add((then, handle));
                Waiting.Add(handle);
                return handle;
            }, () => Changed++);
        }

        public int Timers => timers.Count(t => !t.Handle.Stopped);

        public void Fire() => timers.Where(t => !t.Handle.Stopped).ToList().ForEach(t =>
        {
            t.Handle.Dispose();
            t.Then();
        });
    }

    static readonly Guid Task1 = Guid.NewGuid();

    [Fact]
    public void Nothing_exists_before_a_page_is_followed()
    {
        var rig = new Rig();

        Assert.Null(rig.Poll.Task);
        Assert.Equal(0, rig.Timers);
        Assert.Equal(0, rig.Poll.ReadersMade);
        Assert.False(rig.Poll.Alive);
    }

    [Fact]
    public void Following_a_page_reads_at_once_and_then_every_two_seconds()
    {
        var rig = new Rig();

        rig.Poll.Follow(Task1);
        Assert.Equal(1, rig.Poll.Reads);
        Assert.Equal(1, rig.Timers);

        rig.Fire();
        rig.Fire();
        Assert.Equal(3, rig.Poll.Reads);
        Assert.Equal(1, rig.Poll.ReadersMade);
        Assert.All(rig.Read, path => Assert.Equal(@"C:\made\up\a.jsonl", path));
    }

    [Fact]
    public void The_same_task_again_changes_nothing_and_another_one_starts_over()
    {
        var rig = new Rig();
        rig.Poll.Follow(Task1);
        var reads = rig.Poll.Reads;

        rig.Poll.Follow(Task1);
        Assert.Equal(reads, rig.Poll.Reads);

        rig.Poll.Follow(Guid.NewGuid());
        Assert.Equal(reads + 1, rig.Poll.Reads);
        Assert.Equal(2, rig.Poll.ReadersMade);
        Assert.Equal(1, rig.Timers);
    }

    [Fact]
    public void Stop_ends_the_timer_forgets_the_items_and_leaves_nothing_alive()
    {
        var rig = new Rig { Answer = new ChatUpdate([new ChatMarker("m", ChatMarkerKind.Compacted, Maybe<DateTimeOffset>.None)], true, 10) };
        rig.Poll.Follow(Task1);
        Assert.NotNull(rig.Poll.Latest);

        rig.Poll.Stop();

        Assert.Null(rig.Poll.Task);
        Assert.Null(rig.Poll.Latest);
        Assert.Equal(0, rig.Timers);
        Assert.False(rig.Poll.Alive);
        rig.Fire();
        Assert.Equal(1, rig.Poll.Reads);
    }

    [Fact]
    public void A_paused_poll_reads_nothing_and_a_resumed_one_reads_at_once_and_keeps_its_items()
    {
        var rig = new Rig { Answer = new ChatUpdate([new ChatMarker("m", ChatMarkerKind.Compacted, Maybe<DateTimeOffset>.None)], true, 10) };
        rig.Poll.Follow(Task1);

        rig.Poll.Pause();
        Assert.Equal(0, rig.Timers);
        Assert.NotNull(rig.Poll.Latest);
        rig.Fire();
        Assert.Equal(1, rig.Poll.Reads);

        rig.Poll.Resume();
        Assert.Equal(2, rig.Poll.Reads);
        Assert.Equal(1, rig.Timers);
    }

    [Fact]
    public void A_result_that_arrives_after_the_page_moved_on_is_dropped()
    {
        var rig = new Rig { Deferred = true };
        rig.Poll.Follow(Task1);
        Assert.Single(rig.Pending);

        rig.Poll.Stop();
        rig.Pending.ForEach(p => p());

        Assert.Null(rig.Poll.Latest);
        Assert.Equal(0, rig.Changed - 1);
        Assert.Equal(0, rig.Timers);
    }

    [Fact]
    public void Only_one_read_is_ever_in_flight()
    {
        var rig = new Rig { Deferred = true };
        rig.Poll.Follow(Task1);

        rig.Poll.Tick();
        rig.Poll.Tick();

        Assert.Equal(1, rig.Poll.Reads);
        rig.Pending.ForEach(p => p());
        rig.Fire();
        Assert.Equal(2, rig.Poll.Reads);
    }

    [Fact]
    public void An_idle_read_does_not_tell_the_page_but_a_changed_one_does()
    {
        var rig = new Rig { Answer = new ChatUpdate([], false, 0) };
        rig.Poll.Follow(Task1);
        var after = rig.Changed;

        rig.Fire();
        Assert.Equal(after, rig.Changed);

        rig.Answer = new ChatUpdate([], true, 5);
        rig.Fire();
        Assert.Equal(after + 1, rig.Changed);
    }

    [Fact]
    public void An_ended_session_is_read_once_and_then_only_looked_at()
    {
        var rig = new Rig { Source = new ChatSource(ChatSourceKind.Ended, @"C:\made\up\a.jsonl", "a") };
        rig.Poll.Follow(Task1);

        rig.Fire();
        rig.Fire();
        rig.Fire();

        Assert.Equal(1, rig.Poll.Reads);
        Assert.Equal(1, rig.Timers);
    }

    [Fact]
    public void An_ended_session_that_is_resumed_is_read_again_with_the_same_reader()
    {
        var rig = new Rig { Source = new ChatSource(ChatSourceKind.Ended, @"C:\made\up\a.jsonl", "a") };
        rig.Poll.Follow(Task1);

        rig.Source = new ChatSource(ChatSourceKind.Live, @"C:\made\up\a.jsonl", "a");
        rig.Fire();
        rig.Fire();

        Assert.Equal(3, rig.Poll.Reads);
        Assert.Equal(1, rig.Poll.ReadersMade);
    }

    [Fact]
    public void Another_session_gets_a_new_reader_and_drops_the_old_items()
    {
        var rig = new Rig { Answer = new ChatUpdate([new ChatMarker("m", ChatMarkerKind.Compacted, Maybe<DateTimeOffset>.None)], true, 10) };
        rig.Poll.Follow(Task1);

        rig.Source = new ChatSource(ChatSourceKind.Live, @"C:\made\up\b.jsonl", "b");
        rig.Answer = new ChatUpdate([], true, 0);
        rig.Fire();

        Assert.Equal(2, rig.Poll.ReadersMade);
        Assert.Equal(@"C:\made\up\b.jsonl", rig.Read[^1]);
        Assert.Empty(rig.Poll.Latest!.Items);
    }

    [Theory]
    [InlineData(ChatSourceKind.None)]
    [InlineData(ChatSourceKind.Expired)]
    public void No_session_or_an_expired_one_reads_nothing_and_makes_no_reader(ChatSourceKind kind)
    {
        var rig = new Rig { Source = new ChatSource(kind, null, null) };

        rig.Poll.Follow(Task1);
        rig.Fire();

        Assert.Equal(0, rig.Poll.Reads);
        Assert.Equal(0, rig.Poll.ReadersMade);
    }

    [Fact]
    public void A_session_without_its_file_yet_is_looked_for_again_at_the_next_tick()
    {
        var rig = new Rig { Source = new ChatSource(ChatSourceKind.Live, null, "a") };
        rig.Poll.Follow(Task1);
        Assert.Equal(0, rig.Poll.Reads);

        rig.Source = new ChatSource(ChatSourceKind.Live, @"C:\made\up\a.jsonl", "a");
        rig.Fire();

        Assert.Equal(1, rig.Poll.Reads);
    }

    [Fact]
    public void A_reader_that_throws_is_a_quiet_poll_and_the_next_one_goes_on()
    {
        var calls = 0;
        var timers = new List<Action>();
        var poll = new InboxTranscript(_ => new ChatSource(ChatSourceKind.Live, "p", "a"), () => _ => ++calls == 1 ? throw new InvalidOperationException("x") : new ChatUpdate([], true, 1),
            (work, done) => done(work()), (_, then) =>
            {
                timers.Add(then);
                return new System.Reactive.Disposables.BooleanDisposable();
            }, () => { });

        poll.Follow(Task1);
        timers[^1]();

        Assert.Equal(2, calls);
        Assert.NotNull(poll.Latest);
    }
}
