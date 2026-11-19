using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// tasks.json round trips through JsonTaskStore. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md
// ("Persistence"). Each test works in its own fresh temp directory and removes it afterwards.
public sealed class JsonTaskStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-tasks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(4));

    static TaskBoard Sample()
    {
        var workspace = Guid.NewGuid();
        var free = new TaskItem(Guid.NewGuid(), "Call the bank", Maybe<Guid>.None, T0, T0,
            Maybe<TaskTimer>.None, Maybe<DateTimeOffset>.None, Array.Empty<TimerRecord>());
        var running = new TaskItem(Guid.NewGuid(), "Review the PR", workspace, T0, T0.AddMinutes(1),
            new TaskTimer(T0.AddMinutes(1), TimeSpan.FromMinutes(25)), Maybe<DateTimeOffset>.None, Array.Empty<TimerRecord>());
        var done = new TaskItem(Guid.NewGuid(), "Write the spec", workspace, T0, T0.AddHours(2),
            Maybe<TaskTimer>.None, T0.AddHours(2),
            [new TimerRecord(T0, TimeSpan.FromMinutes(10), T0.AddMinutes(10), TimerEnd.Replaced),
             new TimerRecord(T0.AddMinutes(10), TimeSpan.FromMinutes(30), T0.AddHours(2), TimerEnd.Done)]);
        return new TaskBoard([free, running, done]);
    }

    static void AssertSame(TaskItem expected, TaskItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.WorkspaceId, actual.WorkspaceId);
        Assert.Equal(expected.Created, actual.Created);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.Timer, actual.Timer);
        Assert.Equal(expected.DoneAt, actual.DoneAt);
        Assert.Equal(expected.History, actual.History);
    }

    [Fact]
    public void Round_trips_a_board()
    {
        var store = new JsonTaskStore(dir);
        var board = Sample();

        Assert.True(store.Save(board).IsSuccess);
        var loaded = store.Load();

        Assert.True(loaded.IsSuccess);
        Assert.Equal(board.Tasks.Count, loaded.Value.Tasks.Count);
        board.Tasks.Zip(loaded.Value.Tasks).ToList().ForEach(p => AssertSame(p.First, p.Second));
    }

    [Fact]
    public void A_missing_file_is_an_empty_board()
    {
        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Tasks);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_previous()
    {
        var store = new JsonTaskStore(dir);
        var a = Sample();
        store.Save(a);
        store.Save(new TaskBoard(a.Tasks.Take(1).ToArray()));
        File.WriteAllText(Path.Combine(dir, "tasks.json"), "{not json");

        var loaded = store.Load();

        Assert.True(loaded.IsSuccess);
        Assert.Equal(a.Tasks.Count, loaded.Value.Tasks.Count);
    }

    [Fact]
    public void Times_are_written_in_UTC()
    {
        new JsonTaskStore(dir).Save(Sample());

        var text = File.ReadAllText(Path.Combine(dir, "tasks.json"));

        // Sample() uses +04:00 everywhere; none of that may reach the file.
        Assert.DoesNotContain("+04:00", text);
        Assert.Contains("+00:00", text);
    }
}
