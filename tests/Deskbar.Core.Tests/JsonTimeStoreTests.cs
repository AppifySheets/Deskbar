using Deskbar.Core.Time;

namespace Deskbar.Core.Tests;

// time.json keeps a previous copy the way state.json does (DurableFile): tracked history cannot be rebuilt
// from anywhere else, and before this an unreadable file was quietly replaced by an empty one.
public sealed class JsonTimeStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static readonly Guid Work = Guid.NewGuid();
    static readonly DateOnly Today = new(2026, 10, 23);

    static WorkspaceTime Hours(double hours) => WorkspaceTime.Empty.Credit(Work, Today, TimeSpan.FromHours(hours));

    [Fact]
    public void An_unreadable_time_file_falls_back_to_the_previous_copy()
    {
        var store = new JsonTimeStore(dir);
        store.Save(Hours(1));
        store.Save(Hours(2));
        File.WriteAllBytes(Path.Combine(dir, "time.json"), new byte[2048]);

        var loaded = new JsonTimeStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(TimeSpan.FromHours(1), loaded.Value.On(Work, Today));
    }
}
