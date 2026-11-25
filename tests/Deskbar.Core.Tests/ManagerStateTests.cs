using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// Today's manager session is remembered in state.json (AppState.Manager), so a restart of Deskbar still knows which
// session to attach or resume. These go through the real JsonPersistenceStore: the record carries a DateOnly, and
// only the real serializer proves that round-trips.
public sealed class ManagerStateTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    [Fact]
    public void Manager_round_trips_through_state_json()
    {
        var record = new ManagerRecord("0a1b2c3d-1111-2222-3333-444455556666", new DateOnly(2026, 11, 25));
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { Manager = record }).IsSuccess);

        var loaded = store.Load().Value;

        Assert.Equal(record, loaded.Manager);
    }

    // Step 2's wake fields (ManagerWakes) round-trip too, the hand-back list compared by its contents.
    [Fact]
    public void Manager_wake_fields_round_trip_through_state_json()
    {
        var record = new ManagerRecord("0a1b2c3d-1111-2222-3333-444455556666", new DateOnly(2026, 11, 25),
            new DateTimeOffset(2026, 11, 25, 9, 30, 0, TimeSpan.Zero), Unread: true, HandedBack: [Guid.NewGuid(), Guid.NewGuid()]);
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { Manager = record }).IsSuccess);

        Assert.Equal(record, store.Load().Value.Manager);
    }

    // A record step 1 saved (id and day only) still loads, with no Since, not unread and no hand-backs.
    [Fact]
    public void Step_one_manager_record_loads_without_wake_fields()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Manager":{"SessionId":"0a1b2c3d-1111-2222-3333-444455556666","Day":"2026-11-25"}}""");

        var loaded = new JsonPersistenceStore(dir).Load().Value.Manager;

        Assert.Equal(new ManagerRecord("0a1b2c3d-1111-2222-3333-444455556666", new DateOnly(2026, 11, 25)), loaded);
    }

    [Fact]
    public void Old_state_without_manager_loads_null()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{}");

        Assert.Null(new JsonPersistenceStore(dir).Load().Value.Manager);
    }

    [Fact]
    public void SetManagerSession_persists_the_record()
    {
        var store = new FakeStore();
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        var record = new ManagerRecord("abc", new DateOnly(2026, 11, 25));

        manager.SetManagerSession(record);

        Assert.Equal(record, manager.State.Manager);
        Assert.Equal(record, store.Stored.Manager);
    }
}
