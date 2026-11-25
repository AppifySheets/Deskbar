using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests;

// The task link commands (docs/superpowers/specs/2026-11-25-task-links-design.md, "Commands" and "What a session
// reads"): Petre's `task duplicate`, `task relate`, `task unrelate`; a session's own `duplicate-of`, `relate` and
// `unrelate`; the links in every task's JSON, in the `task join` reply, in `task family` and in the SessionStart
// briefing; and the instruction to look for related work in every briefing.
public class RemoteControlTaskLinksTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly TaskService tasks = new(new MemoryTaskStore(), () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    const string Repo = @"C:\repos\TaskSpaces";

    // The app's transcript lookup, stood in for: every session's transcript is "T:<id>".
    static readonly TaskSessionCommands Sessions = new(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
    {
        Transcript = s => $"T:{s.Id}",
    };

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        var window = new WindowInfo(new WindowHandle(0x10), 100, "Code", @"C:\VS Code\Code.exe", "main.cs - TaskSpaces - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = manager.State.Workspaces.Single(w => w.Name == "Deskbar").DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return manager;
    }

    ControlReply Run(WorkspaceManager manager, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, taskSessions: Sessions).Execute(args);

    ControlReply Said(WorkspaceManager manager, string session, params string[] form) =>
        Run(manager, ["task", .. form, "--session", session, "--from", Repo]);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    Guid Deskbar(WorkspaceManager manager) => manager.State.Workspaces.Single(w => w.Name == "Deskbar").Id;

    TaskItem Get(Guid id) => tasks.Board.Tasks.Single(t => t.Id == id);

    static string Short(TaskItem t) => t.Id.ToString()[..8];

    // --- Petre's forms -----------------------------------------------------------------------------

    [Fact]
    public void Duplicate_closes_the_task_and_the_json_names_the_original()
    {
        var manager = Started();
        var original = tasks.AddNote("Open Claude button", Deskbar(manager)).Value;
        var copy = tasks.AddNote("Open claude without the task", Deskbar(manager)).Value;

        var reply = Run(manager, "task", "duplicate", Short(copy), Short(original));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var json = Json(reply);
        Assert.Equal("closed", json.GetProperty("status").GetString());
        Assert.Equal(original.Id, json.GetProperty("duplicateOf").GetProperty("id").GetGuid());
        Assert.Equal([copy.Id], Json(Run(manager, "task", "list")).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == original.Id)
            .GetProperty("duplicates").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public void Duplicate_of_an_unknown_task_changes_nothing()
    {
        var manager = Started();
        var copy = tasks.AddNote("A note", Deskbar(manager)).Value;

        var reply = Run(manager, "task", "duplicate", Short(copy), "ffffffff");

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.True(Get(copy.Id).IsOpen);
    }

    [Fact]
    public void Relate_and_unrelate_show_in_both_tasks_json()
    {
        var manager = Started();
        var a = tasks.AddNote("A", Deskbar(manager)).Value;
        var b = tasks.AddNote("B", Deskbar(manager)).Value;

        var related = Json(Run(manager, "task", "relate", Short(a), Short(b)));

        Assert.Equal([b.Id], related.GetProperty("related").EnumerateArray().Select(e => e.GetGuid()));
        var other = Json(Run(manager, "task", "list")).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == b.Id);
        Assert.Equal([a.Id], other.GetProperty("related").EnumerateArray().Select(e => e.GetGuid()));

        var unrelated = Json(Run(manager, "task", "unrelate", Short(b), Short(a)));
        Assert.Empty(unrelated.GetProperty("related").EnumerateArray());
    }

    // --- a session's forms -------------------------------------------------------------------------

    [Fact]
    public void A_session_closes_its_own_task_as_a_duplicate_while_live_on_it()
    {
        var manager = Started();
        var original = tasks.AddNote("Open Claude button", Deskbar(manager)).Value;
        var own = tasks.Create("Open claude without the task", Deskbar(manager), tasks.NewSession("s1", Repo)).Value;

        var reply = Said(manager, "s1", "duplicate-of", Short(original));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(TaskStatus.Closed, Get(own.Id).Status);
        Assert.Equal(Maybe.From(original.Id), Get(own.Id).DuplicateOf);
    }

    [Fact]
    public void A_session_relates_its_own_task()
    {
        var manager = Started();
        var other = tasks.AddNote("Other", Deskbar(manager)).Value;
        var own = tasks.Create("Mine", Deskbar(manager), tasks.NewSession("s1", Repo)).Value;

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", "relate", Short(other)).ExitCode);
        Assert.Equal([other.Id], tasks.Board.RelatedOf(own.Id).Select(t => t.Id));

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", "unrelate", Short(other)).ExitCode);
        Assert.Empty(tasks.Board.RelatedOf(own.Id));
    }

    [Fact]
    public void A_session_form_without_its_session_is_refused()
    {
        var manager = Started();
        Assert.Contains("needs --session", Run(manager, "task", "duplicate-of", "abcd").Output);
    }

    // --- what a session reads ----------------------------------------------------------------------

    [Fact]
    public void The_join_reply_lists_the_linked_tasks_with_their_transcripts()
    {
        var manager = Started();
        var done = tasks.Create("Open Claude button", Deskbar(manager), tasks.NewSession("old", Repo)).Value;
        var note = tasks.AddNote("Task links", Deskbar(manager)).Value;
        Assert.True(tasks.Relate(note.Id, done.Id).IsSuccess);

        var linked = Json(Said(manager, "s2", "join", Short(note))).GetProperty("linked").GetString()!;

        Assert.Contains("Related tasks:", linked);
        Assert.Contains($"{Short(done)} \"Open Claude button\"", linked);
        Assert.Contains("transcript T:old", linked);
        Assert.Contains("Before you start, read what the tasks linked to this one did", linked);
    }

    [Fact]
    public void Family_prints_the_links_for_a_task_without_a_family()
    {
        var manager = Started();
        var original = tasks.AddNote("Original", Deskbar(manager)).Value;
        var copy = tasks.AddNote("Copy", Deskbar(manager)).Value;
        Assert.True(tasks.MarkDuplicate(copy.Id, original.Id).IsSuccess);

        var text = Run(manager, "task", "family", Short(original)).Output;

        Assert.Contains("Tasks closed as duplicates of this one:", text);
        Assert.Contains($"{Short(copy)} \"Copy\" (closed as a duplicate)", text);
    }

    [Fact]
    public void Every_briefing_tells_the_session_to_look_for_related_and_duplicate_work()
    {
        var text = SessionBriefing.Start(new ClaudeHookEvent("SessionStart", "s9", Repo, Maybe<string>.None, false),
            Maybe<TaskItem>.None, []);

        Assert.Contains("task list --closed for tasks about the same or related work", text);
        Assert.Contains("task relate <id> --session s9", text);
        Assert.Contains("task duplicate-of <id> --session s9", text);
    }
}
