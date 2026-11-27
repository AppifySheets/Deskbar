using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

public sealed class JsonPersistenceStoreWrittenByTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    [Fact]
    public void Save_stamps_WrittenBy_and_Load_refuses_a_newer_major_stamp()
    {
        new JsonPersistenceStore(dir, runningVersion: "3.0.0").Save(AppState.Empty);
        var older = new JsonPersistenceStore(dir, runningVersion: "2.9.99").Load();
        Assert.True(older.IsFailure);
        Assert.StartsWith("Your settings were saved by Deskbar 3.0.0", older.Error);
    }

    // The Store copy lags the GitHub copy by a patch or a minor, and must still start (StateVersionGate).
    [Fact]
    public void A_newer_stamp_within_the_major_loads()
    {
        new JsonPersistenceStore(dir, runningVersion: "2.2.0").Save(AppState.Empty);
        var older = new JsonPersistenceStore(dir, runningVersion: "2.1.49").Load();
        Assert.True(older.IsSuccess);
        Assert.Equal("2.2.0", older.Value.WrittenBy);
    }

    [Fact]
    public void An_older_stamp_loads_and_is_kept_in_the_state()
    {
        new JsonPersistenceStore(dir, runningVersion: "2.1.40").Save(AppState.Empty);
        var newer = new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load();
        Assert.True(newer.IsSuccess);
        Assert.Equal("2.1.40", newer.Value.WrittenBy);
    }

    [Fact]
    public void Without_a_running_version_nothing_is_stamped_or_refused()
    {
        new JsonPersistenceStore(dir).Save(AppState.Empty);
        Assert.DoesNotContain("WrittenBy\": \"", File.ReadAllText(Path.Combine(dir, "state.json")));
        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load().IsSuccess);
    }

    // A newer build wrote a top-level field this build does not know. Loading and saving it again (what an older
    // Store copy does on a machine that also runs the GitHub copy) must leave the field in the file
    // (AppState.Extra).
    [Fact]
    public void An_unknown_top_level_field_survives_a_load_and_a_save()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.json");
        File.WriteAllText(path, """
            {
              "Workspaces": [], "WorkspaceRules": [], "RenameRules": [], "Inventory": {},
              "WrittenBy": "2.3.0",
              "FutureSetting": { "Mode": "Bold", "Count": 3 }
            }
            """);

        var store = new JsonPersistenceStore(dir, runningVersion: "2.2.0");
        var loaded = store.Load();
        Assert.True(loaded.IsSuccess);
        Assert.True(store.Save(loaded.Value).IsSuccess);

        var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("Bold", saved.GetProperty("FutureSetting").GetProperty("Mode").GetString());
        Assert.Equal(3, saved.GetProperty("FutureSetting").GetProperty("Count").GetInt32());
        // The stamp is the saving build's own, not the newer one it read.
        Assert.Equal("2.2.0", saved.GetProperty("WrittenBy").GetString());
        // And the bag itself never shows up as a key of its own.
        Assert.False(saved.TryGetProperty("Extra", out _));
    }

    [Fact]
    public void A_file_with_no_stamp_loads()
    {
        // state.json written before WrittenBy existed.
        new JsonPersistenceStore(dir).Save(AppState.Empty);
        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load().IsSuccess);
    }

    string[] Copies => Directory.GetFiles(dir, "state.json.from-*");

    // Deskbar task 0bd17709: the installed 2.6.48 loaded a file a 2.6.208 test build wrote and its save dropped every
    // bookmark. An older build now copies the newer file aside before it can save over it.
    [Fact]
    public void A_newer_stamp_within_the_major_is_copied_aside_before_loading()
    {
        new JsonPersistenceStore(dir, runningVersion: "2.6.208+45624cc269").Save(AppState.Empty);
        var written = File.ReadAllText(Path.Combine(dir, "state.json"));

        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.6.48+4cfbd7d2ea").Load().IsSuccess);

        var copy = Assert.Single(Copies);
        Assert.StartsWith("state.json.from-2.6.208+45624cc269.", Path.GetFileName(copy));
        Assert.Equal(written, File.ReadAllText(copy));
    }

    // The review's case: the newer build writes again after the older one stripped the file. The first copy, the one
    // with the data, must not be replaced; an unchanged file is not copied twice.
    [Fact]
    public void A_copy_is_never_overwritten_and_an_unchanged_file_is_not_copied_again()
    {
        var newer = new JsonPersistenceStore(dir, runningVersion: "2.6.208");
        var older = new JsonPersistenceStore(dir, runningVersion: "2.6.48");
        newer.Save(AppState.Empty with { TasksMatrix = true });
        var first = File.ReadAllText(Path.Combine(dir, "state.json"));
        Assert.True(older.Load().IsSuccess);
        Assert.True(older.Load().IsSuccess);
        Assert.Single(Copies);

        newer.Save(AppState.Empty);
        Assert.True(older.Load().IsSuccess);

        Assert.Equal(2, Copies.Length);
        Assert.Contains(Copies, c => File.ReadAllText(c) == first);
    }

    // state.json could not be read, so the load fell back to state.json.previous, which the newer build wrote.
    [Fact]
    public void The_previous_copy_is_kept_when_the_load_fell_back_to_it()
    {
        new JsonPersistenceStore(dir, runningVersion: "2.6.208").Save(AppState.Empty);
        File.Move(Path.Combine(dir, "state.json"), Path.Combine(dir, "state.json.previous"));
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json");

        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.6.48").Load().IsSuccess);

        Assert.Contains("2.6.208", File.ReadAllText(Assert.Single(Copies)));
    }

    [Fact]
    public void Copies_older_than_thirty_days_go()
    {
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "state.json.from-2.6.100.20261001-120000");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
        new JsonPersistenceStore(dir, runningVersion: "2.6.208").Save(AppState.Empty);

        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.6.48").Load().IsSuccess);

        Assert.DoesNotContain(old, Copies);
        Assert.Single(Copies);
    }

    [Theory]
    [InlineData("2.6.48")]   // the same build
    [InlineData("2.6.40")]   // an older build: the usual case after an update
    public void A_stamp_that_is_not_newer_makes_no_copy(string writer)
    {
        new JsonPersistenceStore(dir, runningVersion: writer).Save(AppState.Empty);
        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.6.48").Load().IsSuccess);
        Assert.Empty(Copies);
    }

    // A newer build added a field INSIDE a workspace and inside a bookmark. An older build's load and save keeps both
    // (Workspace.Extra, Bookmark.Extra), where before only top-level fields survived.
    [Fact]
    public void Unknown_fields_inside_a_workspace_and_a_bookmark_survive_a_load_and_a_save()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.json");
        File.WriteAllText(path, """
            {
              "Workspaces": [ {
                "Id": "11111111-1111-1111-1111-111111111111", "Name": "Work", "DesktopId": null,
                "FutureList": [ { "Url": "https://example.com" } ],
                "Bookmarks": [ {
                  "Id": "22222222-2222-2222-2222-222222222222", "Name": "Docs", "Kind": "Link",
                  "Target": "https://example.com", "FutureFlag": true
                } ]
              } ],
              "WorkspaceRules": [], "RenameRules": [], "Inventory": {}, "WrittenBy": "2.7.0"
            }
            """);

        var store = new JsonPersistenceStore(dir, runningVersion: "2.6.61");
        var loaded = store.Load();
        Assert.True(loaded.IsSuccess);
        // An ordinary edit, through `with`, as every edit is made.
        var renamed = loaded.Value with
        {
            Workspaces = loaded.Value.Workspaces.Select(w => w with
            {
                Name = "Renamed",
                Bookmarks = w.Bookmarks.Select(b => b with { Name = "Manual" }).ToValueList(),
            }).ToList(),
        };
        Assert.True(store.Save(renamed).IsSuccess);

        var workspace = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Workspaces")[0];
        Assert.Equal("Renamed", workspace.GetProperty("Name").GetString());
        Assert.Equal("https://example.com", workspace.GetProperty("FutureList")[0].GetProperty("Url").GetString());
        var bookmark = workspace.GetProperty("Bookmarks")[0];
        Assert.Equal("Manual", bookmark.GetProperty("Name").GetString());
        Assert.True(bookmark.GetProperty("FutureFlag").GetBoolean());
        Assert.False(workspace.TryGetProperty("Extra", out _));
        Assert.False(bookmark.TryGetProperty("Extra", out _));
    }
}
