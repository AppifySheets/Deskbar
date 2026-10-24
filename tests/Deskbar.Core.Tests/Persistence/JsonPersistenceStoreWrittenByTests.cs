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
}
