using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.QuickFind;
using Deskbar.Core.Rules;

namespace Deskbar.Core.Tests;

// Where a quick find launch would land, shown on its row before anything runs (Petre: "including which deskbar
// it will run in, before it runs"), and the start itself, which holds the new window to that workspace.
public class LaunchDestinationTests
{
    const string Rider = @"C:\apps\rider64.exe";
    static readonly Guid Dev = Guid.NewGuid(), Home = Guid.NewGuid();

    // Started, because the manager reads the stored state in Start().
    static WorkspaceManager Manager(AppState state)
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static AppState WithWorkspaces => AppState.Empty with
    {
        Workspaces = [new Workspace(Dev, "Dev", null), new Workspace(Home, "Home", null)],
    };

    [Fact]
    public void A_remembered_app_lands_in_its_remembered_workspace()
    {
        var manager = Manager(WithWorkspaces with
        {
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [Dev] = [new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\X.sln", "X")] },
        });

        Assert.Equal(Dev, manager.LaunchDestination(Rider, $"\"{Rider}\" C:\\src\\X.sln"));
    }

    // Review I1: a pinned app is remembered as pinned, and its row must not force it onto one workspace.
    [Fact]
    public void A_pinned_app_is_reported_as_pinned()
    {
        const string Beeper = @"C:\apps\beeper.exe";
        var manager = Manager(WithWorkspaces with { PinnedApps = [new InventoryEntry(Beeper, null, "Beeper")] });

        Assert.Equal(Rehydration.PlacementKind.Pinned, manager.LaunchPlacement(Beeper, null)!.Kind);
        Assert.Null(manager.LaunchDestination(Beeper, null));
    }

    // Review M2: an app whose windows stay where they are opened has no remembered home to promise.
    [Fact]
    public void A_shell_window_has_no_destination()
    {
        var explorer = @"C:\Windows\explorer.exe";
        var manager = Manager(WithWorkspaces with
        {
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [Dev] = [new InventoryEntry(explorer, null, "x")] },
        });
        Assert.Null(manager.LaunchPlacement(explorer, null));
    }

    [Fact]
    public void A_rule_decides_for_an_app_memory_has_never_seen()
    {
        var manager = Manager(WithWorkspaces with { WorkspaceRules = [new WorkspaceRule(Home, RuleMatchKind.ProcessName, "rider64")] });

        Assert.Equal(Home, manager.LaunchDestination(Rider, null));
    }

    [Fact]
    public void Nothing_known_means_the_current_workspace() =>
        Assert.Null(Manager(WithWorkspaces).LaunchDestination(Rider, null));

    [Fact]
    public void A_start_expects_its_window_in_that_workspace()
    {
        var manager = Manager(WithWorkspaces);
        ProgramLaunch? started = null;

        var result = manager.StartIn(Dev, new LaunchTarget(new ProgramLaunch(Rider, [], null), "rider64"),
            launch => { started = launch; return Result.Success<int?>(42); });

        Assert.True(result.IsSuccess);
        Assert.Equal(Rider, started!.Program);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void A_failed_start_withdraws_the_expectation()
    {
        var manager = Manager(WithWorkspaces);

        var result = manager.StartIn(Dev, new LaunchTarget(new ProgramLaunch(Rider, [], null), "rider64"),
            _ => Result.Failure<int?>("no such program"));

        Assert.True(result.IsFailure);
        Assert.False(manager.AwaitsAny("rider64"));
    }

    [Fact]
    public void A_start_with_nothing_to_expect_registers_no_expectation()
    {
        var manager = Manager(WithWorkspaces);

        Assert.True(manager.StartIn(Dev, new LaunchTarget(new ProgramLaunch("explorer.exe", [@"shell:AppsFolder\X!App"], null), null),
            _ => Result.Success<int?>(null)).IsSuccess);
        Assert.False(manager.AwaitsAny("explorer"));
    }
}
