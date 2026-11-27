using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tasks;

// tasks.json, beside state.json and time.json. Written and read through DurableFile for the same reason as they
// are: neither a crash mid-write nor a restart may destroy what was already written down. Design:
// docs/superpowers/specs/2026-11-22-tasks-board-design.md ("Calls made in the conversation that the builder should
// keep": the migration), with the persistence rules of 2026-11-19-tasks-in-the-bar-design.md kept.
//
// VERSIONS. The file is `{"version":2,"tasks":[...]}`. A file with no `version` is version 1, the timer build's
// (PR #356), and is MIGRATED on load: a live task (running or open) becomes a Backlog note on the same workspace, a
// done one becomes Closed, and timers and their history are dropped; there are no sessions or comments to bring.
// Raised, the sort key, is the task's Created. The version 1 file is KEPT beside the new one as tasks.json.v1, with
// its original bytes, so nothing the timer build wrote is lost to the migration. That copy is made by the first Save
// after a version 1 Load, not by the Load itself: until something is saved, tasks.json is still the version 1 file
// and needs no copy. It is made once; an existing tasks.json.v1 is never overwritten. A version above 2 (a newer
// build) is read as version 2, tolerating what it does not know, rather than failing the whole file.
//
// THE FLAGS AND THE ORDER (Task 12, Rulings 24, 25 and 28). `important`, `urgent` and `rank` are optional fields of
// version 2, not a version 3: an older version 2 build reads the file and ignores them, and drops them on its next save.
// That costs the order and the flags (every task falls back to quadrant IV, in the Raised order FallbackRank gives),
// never a task. A missing flag is off. A missing rank is derived ONCE, on load, from the
// order the bar showed before ranks existed (Waiting on you, then Done, then the rest, each latest raised first;
// FallbackRank), so the first start of this build moves no line, and the first save writes the ranks down.
//
// THE GOAL (goals plan, Task 2; docs/superpowers/specs/2026-11-22-goals-planner-design.md). `goalId` is an optional
// field of version 2 too, a Guid or null, the goal the task counts toward. A missing one is no goal. An older version 2
// build reads the file, ignores it and drops it on its next save, which costs the links and never a task. That same
// risk is why the goals themselves are in a file of their own, goals.json (spec, "Calls made in writing this up"): an
// older build put back by the updater never touches it, so at worst the links go and the goals stay.
//
// THE PARENT (subtasks, docs/superpowers/specs/2026-11-24-subtasks-design.md). `parentId` is an optional field of version
// 2, a Guid or null, the task this one is a subtask of. A missing one is no parent. An older version 2 build ignores it
// and drops it on its next save, which costs the links and never a task, as with `goalId`.
//
// TASK LINKS (docs/superpowers/specs/2026-11-25-task-links-design.md). `duplicateOf` (a Guid or null) and `related` (an
// array of Guids, null for none) are optional fields of version 2 as well; an older build drops them on its next save,
// which costs the links and never a task.
//
// THE ASSIGNEE (Ruling 57). `assignee` is an optional object of version 2 too, `{"kind":"me|claude|colleague","id":..,"name":..}`,
// so no version bump. A missing or unreadable one is DERIVED on load (Assignee.DerivedFor): a task with sessions that is
// in progress or in the backlog goes to Claude, everything else to Petre. An older build ignores the field and drops it
// on its next save, which costs the assignments and never a task.
//
// SETTLED COMMENTS (Ruling 62). A comment's optional `settled` time, null for none, in version 2 as well; an older build drops
// it on its next save, which costs the folding and never a comment.
//
// THE DESCRIPTION (Ruling 58). `description` is an optional string of version 2 as well, Petre's own markdown under the
// title; missing or null reads as none, and none is written as null, so a file without descriptions is unchanged. An
// older build ignores it and drops it on its next save, which costs the text and never a task.
//
// RECURRING TASKS (task 0b78269e, docs/superpowers/specs/2026-11-26-recurring-tasks-design.md). `repeat`, an object
// `{"schedule":"<canonical text>","model":"...","folder":"..."}`, and `nextRun`, a UTC time, are optional fields of version 2
// as well, both left out of the file for a task that does not repeat. Read tolerantly: anything but an object of strings
// reads as no repeat, and so does a schedule that does not parse, a model `claude` would not take or a blank folder, so one
// hand edit never fails the file or invents a schedule; an unreadable `nextRun` reads as none. Still version 2; an older
// build drops them on its next save, which costs the schedule and never a task.
//
// THE ANSWER OPTIONS (the inbox's answer buttons). `questionOptions` is an optional array of strings of version 2 as well,
// the answers a session offered with the open question; missing, null or of the wrong type reads as none, and none is not
// written, so a file without any is unchanged. WHY A FILE WITH THEM STILL LOADS IN A BUILD THAT DOES NOT KNOW THEM: the
// reader's JsonSerializerOptions are the Web defaults, which do not disallow unmapped members, so a property no DTO field
// names is skipped whole and never fails the parse (the same way every optional field above reached older builds; a
// test, "A_file_with_an_unknown_field_still_loads", holds the options to that). That older build ignores the list and
// drops it at its next save, which costs the buttons of the one open question, never a task.
//
// The model uses Maybe, which serialises badly, so the file is written from private DTO records: a Maybe becomes a
// nullable field (null means none). Every time is converted to UTC before writing AND after reading, so the file
// means the same on a machine in another time zone, a hand-edited time in local time is still the same instant,
// and a later sync can compare UpdatedAt directly.
//
// A status is written by NAME and read tolerantly (TolerantStatusConverter), the way state.json's strip items are
// (KnownNamesOnlyConverter): a name a newer build added reads as Backlog instead of failing the whole file, because
// a failed parse is what makes the file unreadable, and an unreadable file is what the next paragraph has to rescue.
//
// AN UNREADABLE FILE IS KEPT, NEVER SAVED OVER. When tasks.json and its previous copy are both unreadable, Load fails
// and the service starts from an empty board; the next save would then replace tasks.json and the one after it would
// rotate that over tasks.json.previous, so two clicks would destroy both. So a failed Load moves the unreadable files
// aside as `<name>.<yyyyMMdd-HHmmss>.bak` before anything can be saved (DurableFile.KeepAside, shared with goals.json
// since the goals planner). That happens HERE, inside Load, rather than as
// a Quarantine() the service calls, because ITaskStore is the seam a cloud store replaces (see ITaskStore) and must
// stay its two members: only a store made of files has files to move aside.
//
// `trace` hears what goes wrong without failing anything: today only a .v1 copy that could not be written (KeepVersion1).
//
// `write` puts the serialized text on the disk, as for state.json (JsonPersistenceStore): null is DurableFile.Write,
// synchronous; the app passes DeferredWrites.Write so the flush happens off the UI thread.
public sealed class JsonTaskStore(string baseDirectory, Action<string>? trace = null, Action<string, string>? write = null) : ITaskStore
{
    const int CurrentVersion = 2;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new TolerantStatusConverter() },
    };

    // --- version 2, what this build writes ---
    // Text fields are nullable because a hand-edited file can say `"title": null`; FromDto makes that "". A missing
    // Raised reads as Created, missing lists as empty, a missing status as Backlog (the enum's default).

    // ReplacedAt: TaskSession.ReplacedAt (Ruling 19, /clear). Left out of the file when there is none, so a file
    // written before it existed and one written now read the same way, and every ordinary session stays as short as it
    // was. Still version 2.
    sealed record SessionDto(string? Id, string? Folder, DateTimeOffset Joined, DateTimeOffset? Ended,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReplacedAt = null);

    // Asks: TaskComment.Asks (task 968c2216), the question an ask wrote into the thread. Left out of the file unless set,
    // so every other comment is written as before and an older build simply drops it (the comment stays, as a plain one).
    sealed record CommentDto(Guid Id, string? SessionId, DateTimeOffset At, string? Markdown,
        [property: JsonConverter(typeof(TolerantTimeConverter))] DateTimeOffset? Settled = null,
        [property: JsonConverter(typeof(TolerantFlagConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Asks = null);

    sealed record TaskDto(
        Guid Id,
        string? Title,
        Guid? WorkspaceId,
        TaskStatus Status,
        string? StatusLine,
        string? Question,
        DateTimeOffset Created,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? Raised,
        IReadOnlyList<SessionDto>? Sessions,
        IReadOnlyList<CommentDto>? Comments,
        bool? Important = null,
        bool? Urgent = null,
        double? Rank = null,
        Guid? GoalId = null,
        [property: JsonConverter(typeof(TolerantAssigneeConverter))] AssigneeDto? Assignee = null,
        [property: JsonConverter(typeof(TolerantStringConverter))] string? Description = null,
        [property: JsonConverter(typeof(TolerantTimeConverter))] DateTimeOffset? WaitingSince = null,
        // TaskItem.Adopted; missing in a file written before it existed, which reads as false. Still version 2.
        bool? Adopted = null,
        // TaskItem.HandedBack (stalled tasks, spec 2026-11-23-stalled-claude-tasks-design.md): optional, read tolerantly
        // like WaitingSince, so a wrong-typed value reads as not handed back. Still version 2.
        [property: JsonConverter(typeof(TolerantTimeConverter))] DateTimeOffset? HandedBack = null,
        // TaskItem.QuestionSetAside (task 8bd18246): the question Deskbar set aside while Claude works after Petre's reply,
        // or null. Read tolerantly like Description, so a wrong-typed value reads as none. Still version 2.
        [property: JsonConverter(typeof(TolerantStringConverter))] string? QuestionSetAside = null,
        // TaskItem.ParentId (subtasks spec, 2026-11-24-subtasks-design.md): the parent task's id or null. Still version 2.
        Guid? ParentId = null,
        // TaskItem.SnoozedUntil (task 6da8d7eb, snoozing): when a snoozed task comes back, or null. Read tolerantly like
        // HandedBack, so a wrong-typed value reads as awake rather than failing the whole file. Still version 2.
        [property: JsonConverter(typeof(TolerantTimeConverter))] DateTimeOffset? SnoozedUntil = null,
        // TaskItem.Pin (task f41d7857, the pinned block's own order): null in a file written before it existed,
        // which reads as the latest raised on top (FallbackPin), the block's order until then. Still version 2.
        double? Pin = null,
        // TaskItem.StatusLog (task 521d2b07, the task stats): every status change since logging began. Left out of the
        // file while empty, so a task nobody moved stays as short as it was; a file without it reads as no history.
        // Still version 2.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatusChangeDto>? StatusLog = null,
        // TaskItem.PullRequests (task e7aa49b1): the pull requests the task's sessions opened, as their URLs. Left out of
        // the file while empty, like StatusLog. Read tolerantly: anything but an array of strings reads as none, and an
        // entry that is not a GitHub pull request link is skipped (FromDto). Still version 2; an older build ignores the
        // field and drops it on its next save, which costs the links and never a task.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantStringListConverter))]
        IReadOnlyList<string>? PullRequests = null,
        // Task links (2026-11-25-task-links-design.md): TaskItem.DuplicateOf, the original's id or null, and
        // TaskItem.Related, the ids linked from this task or null for none (so a file without links is unchanged).
        // Still version 2.
        Guid? DuplicateOf = null,
        IReadOnlyList<Guid>? Related = null,
        // TaskItem.Deployed (task 8470d176, phase 2): the recorded pull requests reported running where their app is
        // deployed, each as {key, at, note}. Left out of the file while empty, like PullRequests, and read as tolerantly:
        // anything but an array reads as none, and an entry without a key, a time and a note is skipped
        // (TolerantDeployedListConverter). Still version 2; an older build ignores the field and drops it on its next
        // save, which costs the reports (those tasks go back to the hollow ring) and never a task.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantDeployedListConverter))]
        IReadOnlyList<DeployedDto>? Deployed = null,
        // TaskItem.Repeat and TaskItem.NextRun (task 0b78269e, recurring tasks): the schedule with its model and folder, and
        // when the next run is due. Both left out while none, both read tolerantly (see the header). Still version 2.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantRepeatConverter))]
        RepeatDto? Repeat = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantTimeConverter))]
        DateTimeOffset? NextRun = null,
        // TaskItem.QuestionOptions (the inbox's answer buttons): the answers offered with the open question, as strings. Left
        // out of the file while empty, so a task without any is written as before. Read tolerantly like PullRequests: anything
        // but an array reads as none and a non-string entry is skipped (FromDto then keeps them only with a question). Still
        // version 2: an older build's reader skips a property it does not know (the options never fail the load; see the
        // header), and drops it on its next save, which costs the buttons of one open question and never a task.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantStringListConverter))]
        IReadOnlyList<string>? QuestionOptions = null,
        // TaskItem.DeployPullRequests (task 1565789d): the task's deploy pull requests as URLs, read like PullRequests; and
        // TaskItem.DeployPullRequestsEnded, the keys of those whose merge or close the session was told. Both left out of
        // the file while empty and read tolerantly. Still version 2: an older build drops them on its next save, which
        // costs the watch of a deploy pull request (the session watches it as before) and never a task.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantStringListConverter))]
        IReadOnlyList<string>? DeployPullRequests = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantStringListConverter))]
        IReadOnlyList<string>? DeployPullRequestsEnded = null,
        // TaskItem.QuestionIsDeploy (task a87891ca): the question is a deploy hand-over. Written only when true, so a task
        // with a plain question is written as before, and read tolerantly like the other flags. Still version 2: an older
        // build drops it on its next save, which draws one hand-over as a plain question and never costs a task.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TolerantFlagConverter))]
        bool? QuestionIsDeploy = null);

    // TaskItem.Repeat as stored: the schedule's canonical text (RepeatSchedule.Text), the model and the folder. Every part
    // is nullable because the reader fills what it finds and FromDto decides whether that is a repeat.
    sealed record RepeatDto(string? Schedule, string? Model, string? Folder);

    // One reported deploy (TaskItem.Deployed): the pull request's key (PullRequestLink.Key), when, and the evidence.
    sealed record DeployedDto(string Key, DateTimeOffset At, string Note);

    // One status change (TaskItem.StatusLog). Both statuses go through the tolerant converter, like the task's own.
    sealed record StatusChangeDto(DateTimeOffset At, TaskStatus From, TaskStatus To);

    // The assignee (Ruling 57): kind by name ("me", "claude", "colleague"), plus the id and name a colleague needs.
    sealed record AssigneeDto(string? Kind, string? Id, string? Name);

    sealed record FileDto(int Version, IReadOnlyList<TaskDto>? Tasks);

    // --- version 1, read only to migrate: just the fields the migration keeps (timer and history are ignored) ---

    sealed record Version1TaskDto(Guid Id, string? Title, Guid? WorkspaceId, DateTimeOffset Created, DateTimeOffset UpdatedAt, DateTimeOffset? DoneAt);

    sealed record Version1FileDto(IReadOnlyList<Version1TaskDto>? Tasks);

    // Only `version` is read first, to choose which shape to parse the rest as; no version means 1.
    sealed record VersionProbe(int? Version);

    sealed record Parsed(TaskBoard Board, bool WasVersion1);

    string TasksPath => Path.Combine(baseDirectory, "tasks.json");

    string Version1Path => TasksPath + ".v1";

    // The bytes of the version 1 file the last Load migrated, until the next Save keeps them as tasks.json.v1. The
    // store's one piece of state: it carries "what was loaded" from Load to Save, which the two-member seam has no
    // other way to say.
    Maybe<byte[]> version1Original = Maybe<byte[]>.None;

    // A missing file is the normal first run. An unreadable one falls back to the previous copy
    // (DurableFile.Read); only when both are unreadable is it a failure, and then the unreadable files are moved
    // aside (see the header) and the failure says where they went, which App traces.
    public Result<TaskBoard> Load() =>
        DurableFile.Read(TasksPath, Parse, new Parsed(TaskBoard.Empty, false))
            .MapError(error => $"{error}; {DurableFile.KeepAside(TasksPath)}")
            .Tap(parsed => version1Original = parsed.WasVersion1 ? Version1Source() : Maybe<byte[]>.None)
            .Map(parsed => parsed.Board);

    public Result Save(TaskBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            KeepVersion1();
            // Flushed and swapped in with the old file kept as tasks.json.previous, as state.json is.
            (write ?? DurableFile.Write)(TasksPath, JsonSerializer.Serialize(new FileDto(CurrentVersion, board.Tasks.Select(ToDto).ToArray()), Options));
        }, e => $"Could not write {TasksPath}: {e.Message}");

    static Parsed Parse(string text) =>
        (JsonSerializer.Deserialize<VersionProbe>(text, Options) ?? throw new JsonException("tasks.json deserialized to null")).Version is null or 1
            ? new Parsed(new TaskBoard((JsonSerializer.Deserialize<Version1FileDto>(text, Options)?.Tasks ?? []).Select(FromVersion1).ToArray()), true)
            : new Parsed(new TaskBoard((JsonSerializer.Deserialize<FileDto>(text, Options)?.Tasks ?? []).Select(FromDto)
                // Set-aside lines whose question is long gone fold on load too (task aa42794f), not only at the next change.
                .Select(t => t.WithSetAsideNotesFolded()).ToArray()), false);

    // Which file the migrated board came from. DurableFile.Read parses tasks.json when it can; when it cannot, it
    // moves tasks.json aside and parses tasks.json.previous. So a tasks.json still in place is the one that was read.
    // A read that fails here (the file was locked a moment after parsing) only costs the .v1 copy: the board is
    // loaded, and the first save still rotates the version 1 file into tasks.json.previous.
    Maybe<byte[]> Version1Source() =>
        Result.Try(() => File.ReadAllBytes(File.Exists(TasksPath) ? TasksPath : DurableFile.PreviousOf(TasksPath)))
            .Match(bytes => Maybe.From(bytes), _ => Maybe<byte[]>.None);

    // Writes tasks.json.v1 once, before the first version 2 save replaces the version 1 file. Never over an existing
    // copy: that one is the first migration's, and a later version 1 file (restored by hand) must not replace it.
    //
    // BEST EFFORT. The copy is a courtesy to the timer build's file, and the save it runs inside is Petre's change to
    // his tasks: a copy that cannot be written (a lock, a full disk, a folder in the way) is traced and must never
    // fail that save. The bytes stay in memory, so the next save tries the copy again.
    void KeepVersion1()
    {
        if (version1Original.HasNoValue) return;
        var original = version1Original.Value;
        Result.Try(() =>
            {
                if (!File.Exists(Version1Path)) File.WriteAllBytes(Version1Path, original);
            }, e => $"could not keep the version 1 file as {Version1Path}: {e.Message}")
            .Tap(() => version1Original = Maybe<byte[]>.None)
            .TapError(error => trace?.Invoke($"tasks: {error}"));
    }

    static TaskDto ToDto(TaskItem t) =>
        new(t.Id, t.Title,
            t.WorkspaceId.HasValue ? t.WorkspaceId.Value : null,
            t.Status, t.StatusLine,
            t.Question.HasValue ? t.Question.Value : null,
            t.Created.ToUniversalTime(), t.UpdatedAt.ToUniversalTime(), t.Raised.ToUniversalTime(),
            t.Sessions.Select(s => new SessionDto(s.Id, s.Folder, s.Joined.ToUniversalTime(),
                s.Ended.HasValue ? s.Ended.Value.ToUniversalTime() : null,
                s.ReplacedAt.HasValue ? s.ReplacedAt.Value.ToUniversalTime() : null)).ToArray(),
            t.Comments.Select(c => new CommentDto(c.Id, c.SessionId.HasValue ? c.SessionId.Value : null,
                c.At.ToUniversalTime(), c.Markdown, c.Settled.HasValue ? c.Settled.Value.ToUniversalTime() : null, c.Asks ? true : null)).ToArray(),
            t.Important, t.Urgent, t.Rank,
            t.GoalId.HasValue ? t.GoalId.Value : null,
            new AssigneeDto(t.Assignee.Kind.ToString().ToLowerInvariant(), t.Assignee.Id, t.Assignee.Name),
            t.Description.Length > 0 ? t.Description : null,
            t.WaitingSince.HasValue ? t.WaitingSince.Value.ToUniversalTime() : null,
            t.Adopted,
            t.HandedBack.HasValue ? t.HandedBack.Value.ToUniversalTime() : null,
            t.QuestionSetAside.HasValue ? t.QuestionSetAside.Value : null,
            t.ParentId.HasValue ? t.ParentId.Value : null,
            t.SnoozedUntil.HasValue ? t.SnoozedUntil.Value.ToUniversalTime() : null,
            t.Pin,
            t.StatusLog.Count > 0 ? t.StatusLog.Select(c => new StatusChangeDto(c.At.ToUniversalTime(), c.From, c.To)).ToArray() : null,
            t.PullRequests.Count > 0 ? t.PullRequests.Select(p => p.Url).ToArray() : null,
            t.DuplicateOf.HasValue ? t.DuplicateOf.Value : null,
            t.Related.Count > 0 ? t.Related : null,
            t.Deployed.Count > 0 ? t.Deployed.Select(d => new DeployedDto(d.Key, d.At.ToUniversalTime(), d.Note)).ToArray() : null,
            // The schedule is stored as its canonical text, which TaskRepeats.Parse reads back to the same schedule.
            t.Repeat.HasValue ? new RepeatDto(t.Repeat.Value.Schedule.Text, t.Repeat.Value.Model, t.Repeat.Value.Folder) : null,
            t.NextRun.HasValue ? t.NextRun.Value.ToUniversalTime() : null,
            t.QuestionOptions.Count > 0 ? t.QuestionOptions : null,
            t.DeployPullRequests.Count > 0 ? t.DeployPullRequests.Select(p => p.Url).ToArray() : null,
            t.DeployPullRequestsEnded.Count > 0 ? t.DeployPullRequestsEnded : null,
            t.QuestionIsDeploy ? true : null);

    static TaskItem FromDto(TaskDto d) =>
        new(d.Id, d.Title ?? "",
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.Status, d.StatusLine ?? "", Maybe.From(d.Question),
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime(), (d.Raised ?? d.Created).ToUniversalTime(),
            (d.Sessions ?? []).Select(s => new TaskSession(s.Id ?? "", s.Folder ?? "", s.Joined.ToUniversalTime(),
                s.Ended is { } ended ? ended.ToUniversalTime() : Maybe<DateTimeOffset>.None)
            {
                ReplacedAt = s.ReplacedAt is { } replaced ? replaced.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            }).ToArray(),
            (d.Comments ?? []).Select(c => new TaskComment(c.Id, Maybe.From(c.SessionId), c.At.ToUniversalTime(), c.Markdown ?? "")
            { Settled = c.Settled is { } settled ? settled.ToUniversalTime() : Maybe<DateTimeOffset>.None, Asks = c.Asks ?? false }
            // Deskbar's own lines saved before they had an author of their own (TaskComment.DeskbarAuthor).
            .Attributed()).ToArray())
        {
            Important = d.Important ?? false,
            Urgent = d.Urgent ?? false,
            Description = d.Description ?? "",
            WaitingSince = d.WaitingSince is { } since ? since.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            Rank = d.Rank ?? FallbackRank(d.Status, d.Raised ?? d.Created),
            GoalId = d.GoalId is { } goal ? goal : Maybe<Guid>.None,
            // A task saved before the field existed, or with one this build cannot read, gets it derived (Assignee.DerivedFor).
            Assignee = (d.Assignee is { } a ? Assignee.From(a.Kind, a.Id, a.Name) : Maybe<Assignee>.None)
                .GetValueOrDefault(Assignee.DerivedFor((d.Sessions ?? []).Count > 0, d.Status)),
            Adopted = d.Adopted ?? false,
            HandedBack = d.HandedBack is { } handed ? handed.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            QuestionSetAside = string.IsNullOrWhiteSpace(d.QuestionSetAside) ? Maybe<string>.None : d.QuestionSetAside,
            ParentId = d.ParentId is { } parent ? parent : Maybe<Guid>.None,
            SnoozedUntil = d.SnoozedUntil is { } snoozed ? snoozed.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            Pin = d.Pin ?? FallbackPin(d.Raised ?? d.Created),
            StatusLog = (d.StatusLog ?? []).Where(c => c is not null).Select(c => new StatusChange(c.At.ToUniversalTime(), c.From, c.To)).ToArray(),
            // Each stored URL read back as a link; a hand-edited entry that is no pull request link is dropped, and two
            // spellings of one pull request are one.
            PullRequests = (d.PullRequests ?? []).SelectMany(AskedPullRequests.In).DistinctBy(p => p.Key).ToArray(),
            // The deploy pull requests read the same way (task 1565789d); their told keys are kept as written, lower case.
            DeployPullRequests = (d.DeployPullRequests ?? []).SelectMany(AskedPullRequests.In).DistinctBy(p => p.Key).ToArray(),
            DeployPullRequestsEnded = (d.DeployPullRequestsEnded ?? []).Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.ToLowerInvariant()).Distinct().ToArray(),
            DuplicateOf = d.DuplicateOf is { } original ? original : Maybe<Guid>.None,
            Related = (d.Related ?? []).Distinct().ToArray(),
            // Each pull request once, its first report kept (MarkDeployed never records one twice; a hand edit might).
            Deployed = (d.Deployed ?? []).Select(p => new DeployedPullRequest(p.Key, p.At.ToUniversalTime(), p.Note))
                .DistinctBy(p => p.Key).ToArray(),
            Repeat = RepeatOf(d.Repeat),
            NextRun = d.NextRun is { } due ? due.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            // The options only mean something beside a question, and only when they obey the limits (AnswerOptions.Check): a
            // hand-edited list that does not reads as none, as a list with no question does, rather than as buttons the
            // rules would never have let an ask store.
            QuestionOptions = !string.IsNullOrWhiteSpace(d.Question) && AnswerOptions.Check(d.QuestionOptions) is { IsSuccess: true } options
                ? options.Value
                : [],
            // The hand-over mark lives with its question, open or set aside (TaskBoard.HandOverKept); alone it means nothing.
            QuestionIsDeploy = d.QuestionIsDeploy == true && (!string.IsNullOrWhiteSpace(d.Question) || !string.IsNullOrWhiteSpace(d.QuestionSetAside)),
        };

    // A stored repeat as a TaskRepeat, or none (task 0b78269e). It needs a schedule that parses (so a minute-level hand edit
    // under the 15 minute floor is refused here too), a model ClaudeStart.Model accepts (it ends up on a command line)
    // and a folder that is not blank. The model is stored trimmed by ClaudeStart.Model, so a padded one reads cleanly.
    static Maybe<TaskRepeat> RepeatOf(RepeatDto? stored) =>
        stored is { Schedule: { } schedule, Model: { } model, Folder: { } folder }
        && !string.IsNullOrWhiteSpace(folder)
        && TaskRepeats.Parse(schedule) is { IsSuccess: true } parsed
        && ClaudeStart.Model(model) is { IsSuccess: true } accepted
            ? new TaskRepeat(parsed.Value, accepted.Value, folder)
            : Maybe<TaskRepeat>.None;

    // A pin for a task saved before pins existed: the later raised, the higher (smaller is higher), so the pinned block
    // keeps the latest-first order it had and the first save writes it down. A task joining the block goes one above the
    // smallest pin of its part (TaskBoard.TopPin), so it lands on top of these too.
    static double FallbackPin(DateTimeOffset raised) => -raised.ToUnixTimeMilliseconds();

    // The rank a task saved before ranks existed gets, so the tasks keep the order the bar showed them in then:
    // Waiting on you first, then Done, then the rest, each group the latest raised first. Smaller is higher, so the
    // later raised is the more negative, and each earlier group sits a band of 10^13 further up (milliseconds since
    // 1970 are under 2 * 10^12 until the year 2033, and doubles hold these whole numbers exactly). Waiting on you is
    // pinned above the quadrants anyway; its band only decides where such a task lands once it is answered.
    // Every task here has no flags, so all of them are in quadrant IV and this one band order is the whole order.
    static double FallbackRank(TaskStatus status, DateTimeOffset raised) =>
        -raised.ToUnixTimeMilliseconds()
        - status switch
        {
            TaskStatus.WaitingOnYou => 2e13,
            TaskStatus.Done => 1e13,
            _ => 0,
        };

    // The migration (see the header): live becomes a Backlog note, done becomes Closed, nothing else survives.
    static TaskItem FromVersion1(Version1TaskDto d) =>
        new(d.Id, d.Title ?? "",
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.DoneAt.HasValue ? TaskStatus.Closed : TaskStatus.Backlog, "", Maybe<string>.None,
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime(), d.Created.ToUniversalTime(),
            Array.Empty<TaskSession>(), Array.Empty<TaskComment>());

    // TOLERANT READS of the optional fields added after version 2 shipped (review of the assignee batch): a value of the
    // wrong type (`"assignee":"me"`, `{"kind":5}`, `"description":5`, `"settled":"garbage"`), from a hand edit or a newer
    // build, reads as ABSENT (and the assignee is then derived), never as a failed parse: a failed parse makes the whole
    // file unreadable, which is the one thing this file must not do over a single field. Each skips what it cannot use.

    // An object whose `kind`, `id` and `name` are strings; anything else, an object with other types in them included,
    // reads as absent for the field that is wrong (Assignee.From then reads a missing kind as none).
    sealed class TolerantAssigneeConverter : JsonConverter<AssigneeDto?>
    {
        public override AssigneeDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return null;
            }
            string? kind = null, id = null, name = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader.GetString() ?? "";
                reader.Read();
                var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
                if (property.Equals("kind", StringComparison.OrdinalIgnoreCase)) kind = text;
                else if (property.Equals("id", StringComparison.OrdinalIgnoreCase)) id = text;
                else if (property.Equals("name", StringComparison.OrdinalIgnoreCase)) name = text;
            }
            return new AssigneeDto(kind, id, name);
        }

        public override void Write(Utf8JsonWriter writer, AssigneeDto? value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value is null ? null : new { kind = value.Kind, id = value.Id, name = value.Name }, options);
    }

    // A string, or absent for any other type.
    sealed class TolerantStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    // An array's string entries (task e7aa49b1, `pullRequests`); anything that is not an array reads as absent, and an
    // entry that is not a string is skipped, so one odd value never fails the whole file.
    sealed class TolerantStringListConverter : JsonConverter<IReadOnlyList<string>?>
    {
        public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }
            var items = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String) items.Add(reader.GetString()!);
                else reader.Skip();
            }
            return items;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            (value ?? []).ToList().ForEach(writer.WriteStringValue);
            writer.WriteEndArray();
        }
    }

    // The `deployed` array (task 8470d176, phase 2, TaskItem.Deployed): anything that is not an array reads as absent,
    // and an entry that is not an object with a string key, a date-time string at and a string note is skipped, so one
    // hand-edited entry never fails the whole file. The key is kept lower case, as PullRequestLink.Key makes it.
    sealed class TolerantDeployedListConverter : JsonConverter<IReadOnlyList<DeployedDto>?>
    {
        public override IReadOnlyList<DeployedDto>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? null
                : document.RootElement.EnumerateArray().Select(Entry).Where(e => e is not null).Select(e => e!).ToArray();
        }

        // One entry, or null when it lacks any of its three parts (property names in any case, as the Web defaults read).
        static DeployedDto? Entry(JsonElement entry) =>
            entry.ValueKind == JsonValueKind.Object
            && Text(entry, "key") is { Length: > 0 } key
            && Text(entry, "note") is { } note
            && Text(entry, "at") is { } at
            && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? new DeployedDto(key.ToLowerInvariant(), when, note)
                : null;

        // A string property's value by its name in any case, or null when it is missing or not a string.
        static string? Text(JsonElement entry, string name) =>
            entry.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is
                { Value.ValueKind: JsonValueKind.String } property
                ? property.Value.GetString()
                : null;

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<DeployedDto>? value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            (value ?? []).ToList().ForEach(d =>
            {
                writer.WriteStartObject();
                writer.WriteString("key", d.Key);
                writer.WriteString("at", d.At);
                writer.WriteString("note", d.Note);
                writer.WriteEndObject();
            });
            writer.WriteEndArray();
        }
    }

    // The `repeat` object (task 0b78269e, TaskItem.Repeat): anything that is not an object reads as absent, and a part that
    // is not a string reads as missing (property names in any case, as the Web defaults read), so FromDto, which judges
    // the parts, sees null for it. One wrong-typed value never fails the whole file.
    sealed class TolerantRepeatConverter : JsonConverter<RepeatDto?>
    {
        public override RepeatDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return root.ValueKind != JsonValueKind.Object
                ? null
                : new RepeatDto(Text(root, "schedule"), Text(root, "model"), Text(root, "folder"));
        }

        // A string property's value by its name in any case, or null when it is missing or not a string.
        static string? Text(JsonElement entry, string name) =>
            entry.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is
                { Value.ValueKind: JsonValueKind.String } property
                ? property.Value.GetString()
                : null;

        public override void Write(Utf8JsonWriter writer, RepeatDto? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartObject();
            writer.WriteString("schedule", value.Schedule);
            writer.WriteString("model", value.Model);
            writer.WriteString("folder", value.Folder);
            writer.WriteEndObject();
        }
    }

    // A time written as a string that parses, or absent.
    sealed class TolerantTimeConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parsed = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var time) ? time : (DateTimeOffset?)null;
            reader.Skip();
            return parsed;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is { } time) writer.WriteStringValue(time);
            else writer.WriteNullValue();
        }
    }

    // A flag written as true or false, or absent: anything else (a string, a number) reads as absent (task 968c2216,
    // a comment's `asks`), so a hand edit cannot make the whole file unreadable over one flag.
    sealed class TolerantFlagConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parsed = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => (bool?)null };
            reader.Skip();
            return parsed;
        }

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value is { } flag) writer.WriteBooleanValue(flag);
            else writer.WriteNullValue();
        }
    }

    // Reads a TaskStatus from its name in any case and writes it as its name. Anything else, a name a newer build
    // added, a number or null included, reads as Backlog: an open task this build cannot place, which keeps it on
    // the list where Petre sees it. Failing instead would make the whole file unreadable over one word.
    sealed class TolerantStatusConverter : JsonConverter<TaskStatus>
    {
        public override TaskStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            // An object or array where the name should be: passed over whole, so the reader is left after it.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            return Enum.GetValues<TaskStatus>()
                .Where(status => string.Equals(status.ToString(), name, StringComparison.OrdinalIgnoreCase))
                .DefaultIfEmpty(TaskStatus.Backlog)
                .First();
        }

        public override void Write(Utf8JsonWriter writer, TaskStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
