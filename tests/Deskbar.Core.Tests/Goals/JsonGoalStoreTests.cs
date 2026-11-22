using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Tests.Goals;

// goals.json round trips through JsonGoalStore, tolerantly, and an unreadable file is kept aside rather than saved
// over. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Calls made in writing this up": goals in a
// file of their own) and the plan's Task 3 and review focus 3. Each test works in its own fresh temp directory and
// removes it afterwards.
public sealed class JsonGoalStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-goals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(4));

    string GoalsPath => Path.Combine(dir, "goals.json");

    static readonly Guid weekly = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    static readonly Guid monthly = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    static readonly Guid dropped = Guid.Parse("dddddddd-0000-0000-0000-000000000003");

    static GoalBoard Sample() =>
        new GoalBoard([
            new Goal(weekly, "Ship the goals planner", GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), GoalStatus.Open, 0,
                monthly, T0, T0.AddMinutes(5)),
            new Goal(monthly, "Deskbar Pro in the Store", GoalPeriod.MonthOf(new DateOnly(2026, 11, 1)), GoalStatus.Done, 0,
                Maybe<Guid>.None, T0, T0.AddHours(1)),
            new Goal(dropped, "Read Covey", GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), GoalStatus.Dropped, 1,
                Maybe<Guid>.None, T0.AddMinutes(1), T0.AddMinutes(2)),
        ])
        {
            WeekSeen = new DateOnly(2026, 11, 23),
            MonthSeen = new DateOnly(2026, 11, 1),
        };

    // One goal as a person might hand-edit it, with each field given as text.
    static string File1(string kind = "\"week\"", string period = "\"2026-11-23\"", string status = "\"open\"", int version = 1) => $$"""
        {
          "version": {{version}},
          "weekSeen": "2026-11-23",
          "monthSeen": null,
          "goals": [
            { "id": "dddddddd-0000-0000-0000-000000000001", "title": "Ship it", "kind": {{kind}}, "period": {{period}},
              "status": {{status}}, "order": 3, "parentId": null,
              "created": "2026-11-22T09:00:00+04:00", "updatedAt": "2026-11-22T09:30:00+04:00", "aNewerField": [1, 2] }
          ]
        }
        """;

    void WriteGoals(string text)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(GoalsPath, text);
    }

    [Fact]
    public void A_board_round_trips()
    {
        var store = new JsonGoalStore(dir);
        var board = Sample();

        Assert.True(store.Save(board).IsSuccess);
        var loaded = store.Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(board.WeekSeen, loaded.Value.WeekSeen);
        Assert.Equal(board.MonthSeen, loaded.Value.MonthSeen);
        // Goal is a record of values (its period too), so equal goals mean every field came back.
        Assert.Equal(board.Goals, loaded.Value.Goals);
        Assert.All(loaded.Value.Goals, g => Assert.Equal(TimeSpan.Zero, g.Created.Offset));
        Assert.All(loaded.Value.Goals, g => Assert.Equal(TimeSpan.Zero, g.UpdatedAt.Offset));

        var text = File.ReadAllText(GoalsPath);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"weekSeen\": \"2026-11-23\"", text);
        Assert.Contains("\"monthSeen\": \"2026-11-01\"", text);
        Assert.Contains("\"kind\": \"week\"", text);
        Assert.Contains("\"kind\": \"month\"", text);
        Assert.Contains("\"period\": \"2026-11-23\"", text);
        Assert.Contains("\"status\": \"dropped\"", text);
        Assert.Contains("\"parentId\": \"dddddddd-0000-0000-0000-000000000002\"", text);
        // Sample() uses +04:00 everywhere; none of that may reach the file.
        Assert.DoesNotContain("+04:00", text);
        Assert.Contains("+00:00", text);
    }

    [Fact]
    public void A_missing_file_is_an_empty_board()
    {
        var loaded = new JsonGoalStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Goals);
        Assert.True(loaded.Value.WeekSeen.HasNoValue);
        Assert.True(loaded.Value.MonthSeen.HasNoValue);
        // Ruling G1: loading writes nothing, so a copy that never uses goals never has a goals.json.
        Assert.False(File.Exists(GoalsPath));
    }

    // Review M1: one goal with no period (null or missing) or no id is skipped and traced, and the rest of the file
    // reads; failing the whole file over one goal would lose every other goal to the .bak.
    [Fact]
    public void A_goal_without_a_period_or_an_id_is_skipped_and_the_rest_read()
    {
        const string Good = """
            { "id": "dddddddd-0000-0000-0000-000000000009", "title": "Good", "kind": "week", "period": "2026-11-23",
              "created": "2026-11-22T09:00:00+00:00", "updatedAt": "2026-11-22T09:00:00+00:00" }
            """;
        WriteGoals($$"""
            { "version": 1, "goals": [
              { "id": "dddddddd-0000-0000-0000-000000000001", "title": "Null period", "kind": "week", "period": null },
              { "id": "dddddddd-0000-0000-0000-000000000002", "title": "No period", "kind": "month" },
              { "title": "No id", "kind": "week", "period": "2026-11-23" },
              {{Good}} ] }
            """);
        var traced = new List<string>();

        var loaded = new JsonGoalStore(dir, traced.Add).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(["Good"], loaded.Value.Goals.Select(g => g.Title));
        Assert.Equal(3, traced.Count(line => line.Contains("skipped")));
        Assert.Contains(traced, line => line.Contains("Null period"));
        Assert.Contains(traced, line => line.Contains("No id"));
    }

    // A skipped goal is not written back by the next save, so the file it came from is COPIED aside once, as a .bak, and
    // traced: nothing in it is lost. Copied, not moved: goals.json was readable and its other goals are the board, and
    // a crash before the next save must still find them. A second load of the same file makes no second copy.
    [Fact]
    public void A_file_with_a_skipped_goal_is_kept_aside_once()
    {
        const string Text = """
            { "version": 1, "goals": [
              { "id": "dddddddd-0000-0000-0000-000000000001", "title": "No period", "kind": "week" },
              { "id": "dddddddd-0000-0000-0000-000000000009", "title": "Good", "period": "2026-11-23" } ] }
            """;
        WriteGoals(Text);
        var traced = new List<string>();
        var store = new JsonGoalStore(dir, traced.Add);

        Assert.True(store.Load().IsSuccess);
        Assert.True(store.Load().IsSuccess);

        var copy = Assert.Single(Quarantined("goals.json"));
        Assert.Equal(Text, File.ReadAllText(copy));
        Assert.Equal(Text, File.ReadAllText(GoalsPath));
        Assert.Single(traced, line => line.Contains($"kept a copy of goals.json as {Path.GetFileName(copy)}"));
        Assert.Single(traced, line => line.Contains($"already kept as {Path.GetFileName(copy)}"));

        // The next save drops the unusable goal from goals.json; the copy keeps it.
        Assert.True(store.Save(store.Load().Value).IsSuccess);
        Assert.DoesNotContain("No period", File.ReadAllText(GoalsPath));
        Assert.Contains("No period", File.ReadAllText(copy));
    }

    // A file with nothing skipped makes no copy.
    [Fact]
    public void A_clean_file_is_never_copied_aside()
    {
        var store = new JsonGoalStore(dir);
        store.Save(Sample());

        store.Load();

        Assert.Empty(Quarantined("goals.json"));
    }

    // A goal with no times (a hand edit) reads with its created time standing in for both, or the epoch.
    [Fact]
    public void Missing_times_read_as_the_other_time_or_the_epoch()
    {
        WriteGoals("""
            { "version": 1, "goals": [
              { "id": "dddddddd-0000-0000-0000-000000000001", "title": "a", "period": "2026-11-23", "created": "2026-11-22T09:00:00+00:00" },
              { "id": "dddddddd-0000-0000-0000-000000000002", "title": "b", "period": "2026-11-23" } ] }
            """);

        var goals = new JsonGoalStore(dir).Load().Value.Goals;

        Assert.Equal(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero), goals[0].UpdatedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, goals[1].Created);
        Assert.Equal(DateTimeOffset.UnixEpoch, goals[1].UpdatedAt);
    }

    [Fact]
    public void A_hand_edited_file_reads_with_utc_times_and_a_missing_seen_date_as_none()
    {
        WriteGoals(File1());

        var loaded = new JsonGoalStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        var goal = loaded.Value.Goals.Single();
        Assert.Equal("Ship it", goal.Title);
        Assert.Equal(3, goal.Order);
        Assert.True(goal.ParentId.HasNoValue);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 5, 0, 0, TimeSpan.Zero), goal.Created);
        Assert.Equal(TimeSpan.Zero, goal.Created.Offset);
        Assert.Equal(TimeSpan.Zero, goal.UpdatedAt.Offset);
        Assert.Equal(new DateOnly(2026, 11, 23), loaded.Value.WeekSeen.Value);
        Assert.True(loaded.Value.MonthSeen.HasNoValue);
    }

    // A period is its first day; a hand-edited Wednesday reads as the week that holds it.
    [Fact]
    public void A_period_not_on_its_first_day_is_snapped()
    {
        WriteGoals(File1(period: "\"2026-11-25\""));
        Assert.Equal(GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), new JsonGoalStore(dir).Load().Value.Goals.Single().Period);

        WriteGoals(File1(kind: "\"month\"", period: "\"2026-11-25\""));
        Assert.Equal(GoalPeriod.MonthOf(new DateOnly(2026, 11, 1)), new JsonGoalStore(dir).Load().Value.Goals.Single().Period);
    }

    // A status or kind a newer build added must not make the whole file unreadable over one word.
    [Fact]
    public void An_unknown_status_reads_as_open()
    {
        WriteGoals(File1(status: "\"snoozed\""));
        Assert.Equal(GoalStatus.Open, new JsonGoalStore(dir).Load().Value.Goals.Single().Status);

        WriteGoals(File1(status: "\"DONE\""));
        Assert.Equal(GoalStatus.Done, new JsonGoalStore(dir).Load().Value.Goals.Single().Status);

        WriteGoals(File1(status: "null"));
        Assert.Equal(GoalStatus.Open, new JsonGoalStore(dir).Load().Value.Goals.Single().Status);
    }

    [Fact]
    public void An_unknown_kind_reads_as_a_week()
    {
        WriteGoals(File1(kind: "\"quarter\""));
        Assert.Equal(GoalKind.Week, new JsonGoalStore(dir).Load().Value.Goals.Single().Kind);

        WriteGoals(File1(kind: "{ \"odd\": true }"));
        Assert.Equal(GoalKind.Week, new JsonGoalStore(dir).Load().Value.Goals.Single().Kind);

        WriteGoals(File1(kind: "\"Month\""));
        Assert.Equal(GoalKind.Month, new JsonGoalStore(dir).Load().Value.Goals.Single().Kind);
    }

    [Fact]
    public void A_newer_version_is_read_as_this_one()
    {
        WriteGoals(File1(version: 7));

        var loaded = new JsonGoalStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal("Ship it", loaded.Value.Goals.Single().Title);
    }

    [Fact]
    public void A_null_title_reads_as_empty_text()
    {
        WriteGoals(File1().Replace("\"Ship it\"", "null"));

        Assert.Equal("", new JsonGoalStore(dir).Load().Value.Goals.Single().Title);
    }

    // goals.json's own quarantined copies (and its previous copy's): <prefix>.<stamp>.bak, the stamp starting with the year.
    string[] Quarantined(string prefix) =>
        Directory.GetFiles(dir, prefix + ".*.bak").Where(f => Path.GetFileName(f).StartsWith(prefix + ".2", StringComparison.Ordinal)).ToArray();

    // Review focus 3: with goals.json and its previous copy both unreadable, Load fails and the service starts empty.
    // The files are moved aside on that failed Load, so the next saves write a fresh file and never touch them.
    [Fact]
    public void An_unreadable_file_is_kept_aside()
    {
        WriteGoals("{latest, not json");
        File.WriteAllText(GoalsPath + ".previous", "{previous, not json");
        var store = new JsonGoalStore(dir);

        var loaded = store.Load();
        Assert.True(loaded.IsFailure);
        Assert.False(File.Exists(GoalsPath));
        Assert.False(File.Exists(GoalsPath + ".previous"));

        Assert.True(store.Save(Sample()).IsSuccess);
        Assert.True(store.Save(Sample()).IsSuccess);

        var latest = Assert.Single(Quarantined("goals.json"));
        Assert.Equal("{latest, not json", File.ReadAllText(latest));
        var previous = Assert.Single(Quarantined("goals.json.previous"));
        Assert.Equal("{previous, not json", File.ReadAllText(previous));
        Assert.Contains(Path.GetFileName(latest), loaded.Error);
        Assert.Contains(Path.GetFileName(previous), loaded.Error);
        Assert.Equal(Sample().Goals, store.Load().Value.Goals);
    }

    [Fact]
    public void The_previous_copy_rescues_a_broken_file()
    {
        var store = new JsonGoalStore(dir);
        var full = Sample();
        store.Save(full);
        store.Save(full with { Goals = full.Goals.Take(1).ToArray() });
        File.WriteAllText(GoalsPath, "\0\0\0\0");

        var loaded = store.Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(full.Goals, loaded.Value.Goals);
    }

    [Fact]
    public void A_save_that_cannot_write_is_a_failure()
    {
        // The store's folder is a FILE, so creating the folder (and writing into it) cannot succeed.
        Directory.CreateDirectory(dir);
        var blocked = Path.Combine(dir, "not-a-folder");
        File.WriteAllText(blocked, "");

        var saved = new JsonGoalStore(blocked).Save(Sample());

        Assert.True(saved.IsFailure);
        Assert.Contains("goals.json", saved.Error);
    }
}
