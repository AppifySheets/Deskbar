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
// aside as `<name>.<yyyyMMdd-HHmmss>.bak` before anything can be saved. That happens HERE, inside Load, rather than as
// a Quarantine() the service calls, because ITaskStore is the seam a cloud store replaces (see ITaskStore) and must
// stay its two members: only a store made of files has files to move aside.
//
// `trace` hears what goes wrong without failing anything: today only a .v1 copy that could not be written (KeepVersion1).
public sealed class JsonTaskStore(string baseDirectory, Action<string>? trace = null) : ITaskStore
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

    sealed record SessionDto(string? Id, string? Folder, DateTimeOffset Joined, DateTimeOffset? Ended);

    sealed record CommentDto(Guid Id, string? SessionId, DateTimeOffset At, string? Markdown);

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
        IReadOnlyList<CommentDto>? Comments);

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
            .MapError(error => $"{error}; {KeepAside()}")
            .Tap(parsed => version1Original = parsed.WasVersion1 ? Version1Source() : Maybe<byte[]>.None)
            .Map(parsed => parsed.Board);

    public Result Save(TaskBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            KeepVersion1();
            // Flushed and swapped in with the old file kept as tasks.json.previous, as state.json is.
            DurableFile.Write(TasksPath, JsonSerializer.Serialize(new FileDto(CurrentVersion, board.Tasks.Select(ToDto).ToArray()), Options));
        }, e => $"Could not write {TasksPath}: {e.Message}");

    static Parsed Parse(string text) =>
        (JsonSerializer.Deserialize<VersionProbe>(text, Options) ?? throw new JsonException("tasks.json deserialized to null")).Version is null or 1
            ? new Parsed(new TaskBoard((JsonSerializer.Deserialize<Version1FileDto>(text, Options)?.Tasks ?? []).Select(FromVersion1).ToArray()), true)
            : new Parsed(new TaskBoard((JsonSerializer.Deserialize<FileDto>(text, Options)?.Tasks ?? []).Select(FromDto).ToArray()), false);

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

    // Moves tasks.json and tasks.json.previous, whichever exist, out of the way of the next two saves, and says
    // what happened to each. A move that fails (the file is locked) is reported, not thrown: Load must still answer.
    string KeepAside() =>
        string.Join("; ", new[] { TasksPath, DurableFile.PreviousOf(TasksPath) }
            .Where(File.Exists)
            .Select(path => Result.Try(() => MoveAside(path), e => $"could not keep {path} aside: {e.Message}")
                .Match(aside => $"kept {Path.GetFileName(path)} as {Path.GetFileName(aside)}", error => error)));

    // `<path>.<yyyyMMdd-HHmmss>.bak`, with a counter only if that name is taken (two failed starts in one second).
    static string MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var aside = Enumerable.Range(0, int.MaxValue)
            .Select(n => n == 0 ? $"{path}.{stamp}.bak" : $"{path}.{stamp}-{n}.bak")
            .First(candidate => !File.Exists(candidate));
        File.Move(path, aside);
        return aside;
    }

    static TaskDto ToDto(TaskItem t) =>
        new(t.Id, t.Title,
            t.WorkspaceId.HasValue ? t.WorkspaceId.Value : null,
            t.Status, t.StatusLine,
            t.Question.HasValue ? t.Question.Value : null,
            t.Created.ToUniversalTime(), t.UpdatedAt.ToUniversalTime(), t.Raised.ToUniversalTime(),
            t.Sessions.Select(s => new SessionDto(s.Id, s.Folder, s.Joined.ToUniversalTime(),
                s.Ended.HasValue ? s.Ended.Value.ToUniversalTime() : null)).ToArray(),
            t.Comments.Select(c => new CommentDto(c.Id, c.SessionId.HasValue ? c.SessionId.Value : null,
                c.At.ToUniversalTime(), c.Markdown)).ToArray());

    static TaskItem FromDto(TaskDto d) =>
        new(d.Id, d.Title ?? "",
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.Status, d.StatusLine ?? "", Maybe.From(d.Question),
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime(), (d.Raised ?? d.Created).ToUniversalTime(),
            (d.Sessions ?? []).Select(s => new TaskSession(s.Id ?? "", s.Folder ?? "", s.Joined.ToUniversalTime(),
                s.Ended is { } ended ? ended.ToUniversalTime() : Maybe<DateTimeOffset>.None)).ToArray(),
            (d.Comments ?? []).Select(c => new TaskComment(c.Id, Maybe.From(c.SessionId), c.At.ToUniversalTime(), c.Markdown ?? "")).ToArray());

    // The migration (see the header): live becomes a Backlog note, done becomes Closed, nothing else survives.
    static TaskItem FromVersion1(Version1TaskDto d) =>
        new(d.Id, d.Title ?? "",
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.DoneAt.HasValue ? TaskStatus.Closed : TaskStatus.Backlog, "", Maybe<string>.None,
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime(), d.Created.ToUniversalTime(),
            Array.Empty<TaskSession>(), Array.Empty<TaskComment>());

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
