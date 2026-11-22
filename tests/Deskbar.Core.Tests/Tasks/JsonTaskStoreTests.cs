using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// tasks.json round trips through JsonTaskStore, and a version 1 file (the timer build's) comes back as notes.
// Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md ("Calls made in the conversation that the builder
// should keep"). Each test works in its own fresh temp directory and removes it afterwards.
public sealed class JsonTaskStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-tasks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(4));

    string TasksPath => Path.Combine(dir, "tasks.json");

    static TaskBoard Sample()
    {
        var workspace = Guid.NewGuid();
        var note = new TaskItem(Guid.NewGuid(), "Call the bank", Maybe<Guid>.None, TaskStatus.Backlog, "",
            Maybe<string>.None, T0, T0, T0, [], []);
        var waiting = new TaskItem(Guid.NewGuid(), "Tasks redesign", workspace, TaskStatus.WaitingOnYou, "waiting for CI",
            "OK to merge?", T0, T0.AddMinutes(5), T0.AddMinutes(4),
            [new TaskSession("s1", @"C:\repos\TaskSpaces", T0, T0.AddMinutes(3)),
             new TaskSession("s2", @"C:\repos\TaskSpaces", T0.AddMinutes(3), Maybe<DateTimeOffset>.None)],
            [new TaskComment(Guid.NewGuid(), "s2", T0.AddMinutes(4), "The **detail**"),
             new TaskComment(Guid.NewGuid(), Maybe<string>.None, T0.AddMinutes(5), "Go ahead")]);
        var closed = new TaskItem(Guid.NewGuid(), "Write the spec", workspace, TaskStatus.Closed, "",
            Maybe<string>.None, T0, T0.AddHours(2), T0, [], []);
        return new TaskBoard([note, waiting, closed]);
    }

    static void AssertSame(TaskItem expected, TaskItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.WorkspaceId, actual.WorkspaceId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.StatusLine, actual.StatusLine);
        Assert.Equal(expected.Question, actual.Question);
        Assert.Equal(expected.Created, actual.Created);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.Raised, actual.Raised);
        Assert.Equal(expected.Sessions, actual.Sessions);
        Assert.Equal(expected.Comments, actual.Comments);
    }

    [Fact]
    public void A_version_2_file_round_trips()
    {
        var store = new JsonTaskStore(dir);
        var board = Sample();

        Assert.True(store.Save(board).IsSuccess);
        var loaded = store.Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(board.Tasks.Count, loaded.Value.Tasks.Count);
        board.Tasks.Zip(loaded.Value.Tasks).ToList().ForEach(p => AssertSame(p.First, p.Second));

        var text = File.ReadAllText(TasksPath);
        Assert.Contains("\"version\": 2", text);
        Assert.Contains("\"status\": \"WaitingOnYou\"", text);
        Assert.Contains("\"status\": \"Closed\"", text);
        // Sample() uses +04:00 everywhere; none of that may reach the file.
        Assert.DoesNotContain("+04:00", text);
        Assert.Contains("+00:00", text);
    }

    // A literal file as the timer build wrote it: one running task, one open, one done.
    const string Version1 = """
        {
          "tasks": [
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000001",
              "title": "Wait for CI",
              "workspaceId": "11111111-1111-1111-1111-111111111111",
              "created": "2026-11-19T09:00:00+04:00",
              "updatedAt": "2026-11-19T09:05:00+04:00",
              "timer": { "started": "2026-11-19T09:05:00+04:00", "lengthSeconds": 300 },
              "doneAt": null,
              "history": [
                { "started": "2026-11-19T09:00:00+04:00", "lengthSeconds": 60, "ended": "2026-11-19T09:01:00+04:00", "how": "Replaced" }
              ]
            },
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000002",
              "title": "Buy milk",
              "workspaceId": null,
              "created": "2026-11-19T10:00:00+04:00",
              "updatedAt": "2026-11-19T10:00:00+04:00",
              "timer": null,
              "doneAt": null,
              "history": []
            },
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000003",
              "title": "Write the spec",
              "workspaceId": "11111111-1111-1111-1111-111111111111",
              "created": "2026-11-19T08:00:00+04:00",
              "updatedAt": "2026-11-19T11:00:00+04:00",
              "timer": null,
              "doneAt": "2026-11-19T11:00:00+04:00",
              "history": []
            }
          ]
        }
        """;

    [Fact]
    public void A_version_1_file_migrates()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version1);

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        var tasks = loaded.Value.Tasks;
        Assert.Equal([TaskStatus.Backlog, TaskStatus.Backlog, TaskStatus.Closed], tasks.Select(t => t.Status));
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), tasks[0].WorkspaceId.Value);
        Assert.True(tasks[1].WorkspaceId.HasNoValue);
        Assert.Equal(["Wait for CI", "Buy milk", "Write the spec"], tasks.Select(t => t.Title));
        Assert.All(tasks, t => Assert.Empty(t.Sessions));
        Assert.All(tasks, t => Assert.Empty(t.Comments));
        Assert.All(tasks, t => Assert.Equal("", t.StatusLine));
        Assert.All(tasks, t => Assert.Equal(t.Created, t.Raised));
        Assert.All(tasks, t => Assert.Equal(TimeSpan.Zero, t.Created.Offset));
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 5, 0, 0, TimeSpan.Zero), tasks[0].Created);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 5, 5, 0, TimeSpan.Zero), tasks[0].UpdatedAt);
    }

    [Fact]
    public void The_version_1_file_is_kept_beside_the_new_one()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version1);
        var original = File.ReadAllBytes(TasksPath);
        var store = new JsonTaskStore(dir);
        var board = store.Load().Value;

        Assert.False(File.Exists(TasksPath + ".v1"));
        Assert.True(store.Save(board).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(TasksPath + ".v1"));
        Assert.Contains("\"version\": 2", File.ReadAllText(TasksPath));

        // Made once: a later save, even after another version 1 load, leaves the first copy alone.
        File.WriteAllText(TasksPath, Version1.Replace("Buy milk", "Buy bread"));
        Assert.True(store.Save(store.Load().Value).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(TasksPath + ".v1"));
    }

    // The copy is a courtesy, never a reason to lose the save: when tasks.json.v1 cannot be written (here a folder of
    // that name stands in the way) the save still goes through, the failure is traced, and the copy is tried again
    // at the next save.
    [Fact]
    public void A_version_1_copy_that_fails_does_not_fail_the_save()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version1);
        Directory.CreateDirectory(TasksPath + ".v1");
        var traced = new List<string>();
        var store = new JsonTaskStore(dir, traced.Add);

        var saved = store.Save(store.Load().Value);

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error : "");
        Assert.Contains("\"version\": 2", File.ReadAllText(TasksPath));
        Assert.Contains(traced, line => line.Contains("tasks.json.v1"));
    }

    // A version 1 tasks.json that cannot be read falls back to tasks.json.previous (DurableFile.Read); when that copy is
    // version 1 too, it migrates, and the .v1 copy keeps the previous copy's bytes, the file the board came from.
    [Fact]
    public void A_version_1_file_read_through_previous_keeps_the_previous_bytes()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, "{not json");
        File.WriteAllText(TasksPath + ".previous", Version1);
        var previous = File.ReadAllBytes(TasksPath + ".previous");
        var store = new JsonTaskStore(dir);

        var board = store.Load().Value;
        Assert.Equal(3, board.Tasks.Count);
        Assert.True(store.Save(board).IsSuccess);

        Assert.Equal(previous, File.ReadAllBytes(TasksPath + ".v1"));
    }

    [Fact]
    public void A_version_2_load_makes_no_version_1_copy()
    {
        var store = new JsonTaskStore(dir);
        store.Save(Sample());

        store.Save(store.Load().Value);

        Assert.False(File.Exists(TasksPath + ".v1"));
    }

    // A status this build does not know (written by a newer one) reads as Backlog instead of failing the whole file.
    [Fact]
    public void An_unknown_status_reads_as_backlog()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version2(status: "\"Snoozed\""));

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(TaskStatus.Backlog, loaded.Value.Tasks.Single().Status);
    }

    [Fact]
    public void A_status_reads_in_any_case()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version2(status: "\"inprogress\""));

        Assert.Equal(TaskStatus.InProgress, new JsonTaskStore(dir).Load().Value.Tasks.Single().Status);
    }

    // Times are UTC on the way in too, so a hand-edited file in local time means the same instants.
    [Fact]
    public void Times_are_read_as_UTC()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, Version2(status: "\"Done\""));

        var task = new JsonTaskStore(dir).Load().Value.Tasks.Single();

        Assert.Equal(TimeSpan.Zero, task.Created.Offset);
        Assert.Equal(TimeSpan.Zero, task.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, task.Raised.Offset);
        Assert.Equal(TimeSpan.Zero, task.Sessions.Single().Joined.Offset);
        Assert.Equal(TimeSpan.Zero, task.Comments.Single().At.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 5, 0, 0, TimeSpan.Zero), task.Created);
    }

    // A hand-edited file with nulls where text belongs loads as empty text, never a crash; a missing raised time is
    // the created one.
    [Fact]
    public void Nulls_load_as_empty_text()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, """
            { "version": 2, "tasks": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "title": null, "statusLine": null,
              "created": "2026-11-22T09:00:00+00:00", "updatedAt": "2026-11-22T09:00:00+00:00" } ] }
            """);

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        var task = loaded.Value.Tasks.Single();
        Assert.Equal("", task.Title);
        Assert.Equal("", task.StatusLine);
        Assert.Equal(TaskStatus.Backlog, task.Status);
        Assert.Equal(task.Created, task.Raised);
        Assert.Empty(task.Sessions);
        Assert.Empty(task.Comments);
    }

    // One version 2 task in local (+04:00) time, with a session and a comment, as a person might edit it.
    static string Version2(string status) => $$"""
        {
          "version": 2,
          "tasks": [
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000001",
              "title": "Tasks redesign",
              "workspaceId": null,
              "status": {{status}},
              "statusLine": "",
              "question": null,
              "created": "2026-11-22T09:00:00+04:00",
              "updatedAt": "2026-11-22T09:00:00+04:00",
              "raised": "2026-11-22T09:00:00+04:00",
              "sessions": [ { "id": "s1", "folder": "C:/repos/TaskSpaces", "joined": "2026-11-22T09:00:00+04:00", "ended": null } ],
              "comments": [ { "id": "bbbbbbbb-0000-0000-0000-000000000001", "sessionId": "s1", "at": "2026-11-22T09:00:00+04:00", "markdown": "Hi" } ]
            }
          ]
        }
        """;

    [Fact]
    public void A_missing_file_is_an_empty_board()
    {
        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Tasks);
    }

    [Fact]
    public void An_empty_tasks_list_is_an_empty_board()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TasksPath, """{ "version": 2, "tasks": [] }""");

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
        File.WriteAllText(TasksPath, "{not json");

        var loaded = store.Load();

        Assert.True(loaded.IsSuccess);
        Assert.Equal(a.Tasks.Count, loaded.Value.Tasks.Count);
    }

    // The quarantined copies of tasks.json (and of its previous copy) a failed Load moved aside: <prefix>.<stamp>.bak,
    // the stamp starting with the year, which tells tasks.json's own copies from tasks.json.previous's.
    string[] Quarantined(string prefix) =>
        Directory.GetFiles(dir, prefix + ".*.bak").Where(f => Path.GetFileName(f).StartsWith(prefix + ".2", StringComparison.Ordinal)).ToArray();

    // With both copies unreadable, Load fails and the board starts empty, so the next two saves would write over both
    // files. The unreadable files are moved aside on the failed Load, so what was in them survives every later save.
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
}
