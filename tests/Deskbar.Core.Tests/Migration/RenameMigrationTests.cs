using Deskbar.Core.Migration;

namespace Deskbar.Core.Tests.Migration;

// The first start of Deskbar 2.0 on a machine that ran TaskSpaces 1.x: what it carries across, and
// when it refuses to start at all.
//
// Everything that DECIDES is a pure plan computed from facts, so each rule is settled here without a
// registry, a folder or a running 1.x anywhere near it. The thin layer that gathers the facts and
// applies the steps is tested in Deskbar.Windows.Tests (RenameMigratorTests).
public class RenameMigrationTests
{
    // The machine an update from 1.x leaves behind, at the moment the 1.x updater has started 2.0 with
    // --await-previous: the old folder with its files and its shim, the old Run value and no new one,
    // the old mutex already let go of, the real root, and the app itself running. Each test changes
    // only what its scenario is about.
    static readonly RenameFacts AfterTheUpdate = new(
        OldFolderExists: true, NewStateFileExists: false,
        OldFilesPresent: ["state.json", "time.json"],
        OldShimExists: true,
        OldRunValueExists: true, NewRunValueExists: false,
        OldMutexHeld: false, AwaitingPrevious: true,
        RootOverridden: false, IsTheApp: true, Packaged: false);

    const string Refusal = "Exit TaskSpaces from its tray icon first, then start Deskbar.";

    [Fact]
    public void A_first_start_after_the_update_copies_the_old_files_and_swaps_the_run_value() =>
        Assert.Equal(
            [
                new CopyFile("state.json"),
                new CopyFile("time.json"),
                new DeleteOldShim(),
                new DeleteOldRunValue(),
                new SetNewRunValueToThisExe(),
            ],
            RenameMigration.Plan(AfterTheUpdate));

    // The control pipe exposes no Exit, so there is no clean way to stop a running 1.x from here, and
    // two bars on one screen is the thing the whole rename must not cause.
    [Fact]
    public void Starting_beside_a_running_taskspaces_refuses_and_does_nothing_else() =>
        Assert.Equal(
            [new RefuseToStart(Refusal)],
            RenameMigration.Plan(AfterTheUpdate with { OldMutexHeld = true, AwaitingPrevious = false }));

    // The updater's own handover: 1.x started this copy and is on its way out, and that is not a
    // second bar. A relaunch after a crash arrives with the same fact (App.OnStartup passes
    // awaitingPrevious || afterCrash): the crashed copy was a 2.x holding the old name, so no 1.x is
    // running, and the dying copy may let go of the new name before the old one.
    [Fact]
    public void The_updater_handshake_does_not_refuse_even_though_the_old_mutex_is_held() =>
        Assert.DoesNotContain(
            RenameMigration.Plan(AfterTheUpdate with { OldMutexHeld = true, AwaitingPrevious = true }),
            step => step is RefuseToStart);

    [Fact]
    public void A_fresh_machine_has_nothing_to_migrate() =>
        Assert.Empty(RenameMigration.Plan(new RenameFacts(
            OldFolderExists: false, NewStateFileExists: false,
            OldFilesPresent: [],
            OldShimExists: false,
            OldRunValueExists: false, NewRunValueExists: false,
            OldMutexHeld: false, AwaitingPrevious: false,
            RootOverridden: false, IsTheApp: true, Packaged: false)));

    // Once only: a copy made at the first start is never made again, so a second upgrade after a
    // rollback to 1.x does not overwrite what 2.0 has written since. "Once" means until the new
    // state.json exists, and the first start writes it.
    [Fact]
    public void A_second_start_copies_nothing() =>
        Assert.DoesNotContain(
            RenameMigration.Plan(AfterTheUpdate with { NewStateFileExists = true }),
            step => step is CopyFile);

    // The new folder holding nothing but trace.on, made before the first start so that start could be
    // traced. That is not a migrated machine, and treating it as one started the app with no workspaces.
    [Fact]
    public void A_new_folder_with_only_the_trace_marker_still_gets_the_copies() =>
        Assert.Equal(
            [new CopyFile("state.json"), new CopyFile("time.json")],
            RenameMigration.Plan(AfterTheUpdate with { NewStateFileExists = false }).OfType<CopyFile>());

    // Someone who already has Deskbar starting with Windows keeps the path they have.
    [Fact]
    public void An_existing_new_run_value_is_not_overwritten() =>
        Assert.Equal(
            [new DeleteOldRunValue()],
            RenameMigration.Plan(AfterTheUpdate with
            {
                NewRunValueExists = true, OldFolderExists = false, OldFilesPresent = [], OldShimExists = false,
            }));

    // The test host must never touch the real login entry (StartupRegistration's own history), but the
    // file half of the plan is still worth computing there.
    [Fact]
    public void A_test_host_never_touches_the_registry()
    {
        var plan = RenameMigration.Plan(AfterTheUpdate with { IsTheApp = false });

        Assert.DoesNotContain(plan, step => step is DeleteOldRunValue or SetNewRunValueToThisExe);
        Assert.Contains(new CopyFile("state.json"), plan);
    }

    // DESKBAR_APPDATA is a rehearsal against a scratch root, and a rehearsal must not swap the real
    // autostart entry.
    [Fact]
    public void An_overridden_root_never_touches_the_registry()
    {
        var plan = RenameMigration.Plan(AfterTheUpdate with { RootOverridden = true });

        Assert.DoesNotContain(plan, step => step is DeleteOldRunValue or SetNewRunValueToThisExe);
        Assert.Contains(new CopyFile("state.json"), plan);
    }

    // The Store copy's autostart is its package startup task, so a Run value pointing at it would start a
    // second bar at login. The old TaskSpaces value still goes. Store packaging design:
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
    [Fact]
    public void Packaged_rename_migration_sets_no_new_run_value() =>
        Assert.DoesNotContain(RenameMigration.Plan(AfterTheUpdate with { Packaged = true, OldRunValueExists = true }), s => s is SetNewRunValueToThisExe);

    [Fact]
    public void Packaged_rename_migration_still_deletes_the_old_run_value() =>
        Assert.Contains(new DeleteOldRunValue(), RenameMigration.Plan(AfterTheUpdate with { Packaged = true }));
}
