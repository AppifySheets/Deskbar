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

    string TasksPath => Path.Combine(dir, "tasks.json");

    // The quarantined copies of tasks.json (and of its previous copy) a failed Load moved aside: <prefix>.<stamp>.bak,
    // the stamp starting with the year, which tells tasks.json's own copies from tasks.json.previous's.
    string[] Quarantined(string prefix) =>
        Directory.GetFiles(dir, prefix + ".*.bak").Where(f => Path.GetFileName(f).StartsWith(prefix + ".2", StringComparison.Ordinal)).ToArray();

    // Final review I2: with both copies unreadable, Load fails and the board starts empty, so the next two saves
    // would write over both files. The unreadable files are moved aside on the failed Load, so what was in them
    // survives every later save.
    [Fact]
    public void Both_copies_unreadable_are_kept_aside_before_the_next_save()
    {
        var store = new JsonTaskStore(dir);
        store.Save(Sample());
        store.Save(Sample());
        File.WriteAllText(TasksPath, "{latest, not json");
        File.WriteAllText(TasksPath + ".previous", "{previous, not json");

        var loaded = store.Load();
        Assert.True(loaded.IsFailure);

        Assert.True(store.Save(Sample()).IsSuccess);
        Assert.True(store.Save(Sample()).IsSuccess);

        var latest = Assert.Single(Quarantined("tasks.json"));
        Assert.Equal("{latest, not json", File.ReadAllText(latest));
        var previous = Assert.Single(Quarantined("tasks.json.previous"));
        Assert.Equal("{previous, not json", File.ReadAllText(previous));
        Assert.Contains(Path.GetFileName(latest), loaded.Error);
    }

    // The same with no previous copy at all: an unreadable tasks.json on its own is still kept.
    [Fact]
    public void An_unreadable_file_without_a_previous_copy_is_kept_aside()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, "\0\0\0\0");
        var store = new JsonTaskStore(dir);

        Assert.True(store.Load().IsFailure);
        Assert.True(store.Save(Sample()).IsSuccess);

        Assert.Equal("\0\0\0\0", File.ReadAllText(Assert.Single(Quarantined("tasks.json"))));
    }

    [Fact]
    public void A_save_that_cannot_write_is_a_failure()
    {
        // The store's folder is a FILE, so creating the folder (and writing into it) cannot succeed.
        Directory.CreateDirectory(dir);
        var blocked = Path.Combine(dir, "not-a-folder");
        File.WriteAllText(blocked, "");

        var saved = new JsonTaskStore(blocked).Save(Sample());

        Assert.True(saved.IsFailure);
        Assert.Contains("tasks.json", saved.Error);
    }

    // Final review M1: a timer's end is written by name, so the file is readable and a reordered enum cannot
    // renumber it.
    [Fact]
    public void How_a_timer_ended_is_written_by_name()
    {
        new JsonTaskStore(dir).Save(Sample());

        var text = File.ReadAllText(TasksPath);

        Assert.Contains("\"how\": \"Replaced\"", text);
        Assert.Contains("\"how\": \"Done\"", text);
    }

    // A name this build does not know (written by a newer one) reads as Cleared instead of failing the whole file,
    // and the numbers files were written with before names still read.
    [Fact]
    public void An_unknown_end_reads_as_cleared_and_numbers_still_read()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, HandWritten(title: "\"Old\"", how1: "\"Abandoned\"", how2: "3"));

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        var history = loaded.Value.Tasks.Single().History;
        Assert.Equal([TimerEnd.Cleared, TimerEnd.Replaced], history.Select(h => h.How));
    }

    // Times are UTC on the way in too, so a hand-edited file in local time means the same instants.
    [Fact]
    public void Times_are_read_as_UTC()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, HandWritten(title: "\"Old\"", how1: "\"Done\"", how2: "\"Done\""));

        var task = new JsonTaskStore(dir).Load().Value.Tasks.Single();

        Assert.Equal(TimeSpan.Zero, task.Created.Offset);
        Assert.Equal(TimeSpan.Zero, task.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, task.Timer.Value.Started.Offset);
        Assert.All(task.History, h => Assert.Equal(TimeSpan.Zero, h.Started.Offset));
        Assert.All(task.History, h => Assert.Equal(TimeSpan.Zero, h.Ended.Offset));
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 5, 0, 0, TimeSpan.Zero), task.Created);
    }

    // Final review M8: a hand-edited file with a null title loads as an empty title, never a crash.
    [Fact]
    public void A_null_title_loads_as_empty()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, HandWritten(title: "null", how1: "\"Done\"", how2: "\"Done\""));

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal("", loaded.Value.Tasks.Single().Title);
    }

    // One task in local (+04:00) time with a running timer and two history records, as a person might edit it.
    static string HandWritten(string title, string how1, string how2) => $$"""
        {
          "tasks": [
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000001",
              "title": {{title}},
              "workspaceId": null,
              "created": "2026-11-19T09:00:00+04:00",
              "updatedAt": "2026-11-19T09:00:00+04:00",
              "timer": { "started": "2026-11-19T09:00:00+04:00", "lengthSeconds": 300 },
              "doneAt": null,
              "history": [
                { "started": "2026-11-19T08:00:00+04:00", "lengthSeconds": 60, "ended": "2026-11-19T08:01:00+04:00", "how": {{how1}} },
                { "started": "2026-11-19T08:10:00+04:00", "lengthSeconds": 60, "ended": "2026-11-19T08:11:00+04:00", "how": {{how2}} }
              ]
            }
          ]
        }
        """;

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
