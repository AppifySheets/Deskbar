using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

public sealed class JsonPersistenceStoreWrittenByTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    [Fact]
    public void Save_stamps_WrittenBy_and_Load_refuses_a_newer_stamp()
    {
        new JsonPersistenceStore(dir, runningVersion: "2.1.50").Save(AppState.Empty);
        var older = new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load();
        Assert.True(older.IsFailure);
        Assert.StartsWith("Your settings were saved by Deskbar 2.1.50", older.Error);
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

    [Fact]
    public void A_file_with_no_stamp_loads()
    {
        // state.json written before WrittenBy existed.
        new JsonPersistenceStore(dir).Save(AppState.Empty);
        Assert.True(new JsonPersistenceStore(dir, runningVersion: "2.1.44").Load().IsSuccess);
    }
}
