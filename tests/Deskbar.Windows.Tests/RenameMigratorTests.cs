using System.IO;
using Microsoft.Win32;
using Deskbar.App;
using Deskbar.Core.Migration;

namespace Deskbar.Windows.Tests;

// The thin layer under the rename migration: it gathers the facts RenameMigration.Plan decides on and
// applies the steps it returns. The rules themselves are tested in Deskbar.Core.Tests; these check
// that the files land where they should and that the registry is left alone under a test host.
//
// Each test hands RenameMigrator its own temporary root rather than setting DESKBAR_APPDATA. AppPaths
// reads that variable once, when the type is first touched, so setting it inside one test would either
// be ignored or, worse, fix the root for every other test running in the same process.
public class RenameMigratorTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static (object? Old, object? New) RunValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return (key?.GetValue("TaskSpaces"), key?.GetValue("Deskbar"));
    }

    static void WithRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ts-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "TaskSpaces"));
        try { test(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    static string Old(string root, string name) => Path.Combine(root, "TaskSpaces", name);
    static string New(string root, string name) => Path.Combine(root, "Deskbar", name);

    [Fact]
    public void Apply_copies_the_old_state_files_into_the_new_folder_and_leaves_the_old_folder() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            File.WriteAllText(Old(root, "time.json"), "time");

            var applied = RenameMigrator.Apply(root, [new CopyFile("state.json"), new CopyFile("time.json")], _ => { });

            Assert.True(applied.IsSuccess);
            Assert.Equal("state", File.ReadAllText(New(root, "state.json")));
            Assert.Equal("time", File.ReadAllText(New(root, "time.json")));
            Assert.True(File.Exists(Old(root, "state.json")));
            Assert.True(File.Exists(Old(root, "time.json")));
        });

    // state.json is the workspaces. Starting without it is allowed (a fresh file beats a startup that
    // throws) but not silently: the person has to be told the workspaces did not come across and where
    // they still are. The other copies are still made.
    [Fact]
    public void Apply_reports_a_state_file_it_could_not_copy() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            File.WriteAllText(Old(root, "time.json"), "time");

            // Held open with no sharing, so the copy cannot read it.
            using (File.Open(Old(root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var applied = RenameMigrator.Apply(root, [new CopyFile("state.json"), new CopyFile("time.json")], _ => { });

                // A warning, not a refusal: the app shows it and goes on starting.
                Assert.True(applied.IsFailure);
                var warning = Assert.IsType<StateNotCarried>(applied.Error);
                Assert.Contains(Path.Combine(root, "TaskSpaces"), warning.Message);
                Assert.Contains("workspaces", warning.Message);
            }

            Assert.False(File.Exists(New(root, "state.json")));
            Assert.Equal("time", File.ReadAllText(New(root, "time.json")));
        });

    // Any other file is not worth a dialog: the trace has it.
    [Fact]
    public void Apply_traces_but_does_not_report_another_file_it_could_not_copy() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            File.WriteAllText(Old(root, "time.json"), "time");
            var traced = new List<string>();

            using (File.Open(Old(root, "time.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var applied = RenameMigrator.Apply(root, [new CopyFile("state.json"), new CopyFile("time.json")], traced.Add);

                Assert.True(applied.IsSuccess);
            }

            Assert.Contains(traced, line => line.Contains("copying time.json failed"));
            Assert.Equal("state", File.ReadAllText(New(root, "state.json")));
        });

    [Fact]
    public void Apply_deletes_the_old_shim() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "taskspaces.cmd"), "@echo off");

            var applied = RenameMigrator.Apply(root, [new DeleteOldShim()], _ => { });

            Assert.True(applied.IsSuccess);
            Assert.False(File.Exists(Old(root, "taskspaces.cmd")));
            Assert.True(Directory.Exists(Path.Combine(root, "TaskSpaces")));
        });

    // A plan never pairs a refusal with other steps, but Apply does not rely on that: a refusal anywhere
    // in the list means nothing is done.
    [Fact]
    public void Apply_refuses_when_the_plan_says_so_and_touches_nothing() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            File.WriteAllText(Old(root, "taskspaces.cmd"), "@echo off");

            var applied = RenameMigrator.Apply(root,
                [new CopyFile("state.json"), new DeleteOldShim(), new RefuseToStart(RenameMigration.RefusalMessage)],
                _ => { });

            Assert.True(applied.IsFailure);
            Assert.Equal(new Refused(RenameMigration.RefusalMessage), applied.Error);
            Assert.False(Directory.Exists(Path.Combine(root, "Deskbar")));
            Assert.True(File.Exists(Old(root, "taskspaces.cmd")));
        });

    [Fact]
    public void Gather_sees_the_old_folder_and_its_files_under_the_override_root() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            File.WriteAllText(Old(root, "last-used.json"), "{}");
            File.WriteAllText(Old(root, "taskspaces.cmd"), "@echo off");
            File.WriteAllText(Old(root, "trace.on"), "");

            var facts = RenameMigrator.Gather(root, awaitingPrevious: true, oldMutexHeld: false);

            Assert.True(facts.OldFolderExists);
            Assert.False(facts.NewStateFileExists);
            Assert.Equal(["state.json", "last-used.json"], facts.OldFilesPresent);
            Assert.True(facts.OldShimExists);
            Assert.True(facts.AwaitingPrevious);
            Assert.False(facts.OldMutexHeld);
            // A root that is not the real %APPDATA% counts as overridden, and the test host is not the app:
            // either one alone keeps the registry out of the plan.
            Assert.True(facts.RootOverridden);
            Assert.False(facts.IsTheApp);
        });

    // The trap the whole-branch review found: the trace marker lives in the NEW folder, and the
    // briefing tells a session to create it before restarting. Made before the first 2.0 start, it
    // creates %APPDATA%\Deskbar with nothing else in it, and a plan gated on the folder existing would
    // then skip the copy and start with no workspaces, with nothing to recover them later. The gate is
    // the new state.json, so a folder holding only trace.on still gets the copy.
    // The gate is the new state.json and nothing else in the new folder: absent with only the marker
    // there, present once the file is.
    [Fact]
    public void Gather_reports_the_new_state_file_and_not_the_new_folder() =>
        WithRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "Deskbar"));
            File.WriteAllText(New(root, "trace.on"), "");
            File.WriteAllText(New(root, "time.json"), "{}");

            Assert.False(RenameMigrator.Gather(root, awaitingPrevious: false, oldMutexHeld: false).NewStateFileExists);

            File.WriteAllText(New(root, "state.json"), "{}");

            Assert.True(RenameMigrator.Gather(root, awaitingPrevious: false, oldMutexHeld: false).NewStateFileExists);
        });

    [Fact]
    public void A_trace_marker_made_before_the_first_start_does_not_stop_the_copy() =>
        WithRoot(root =>
        {
            File.WriteAllText(Old(root, "state.json"), "state");
            Directory.CreateDirectory(Path.Combine(root, "Deskbar"));
            File.WriteAllText(New(root, "trace.on"), "");

            var plan = RenameMigration.Plan(RenameMigrator.Gather(root, awaitingPrevious: false, oldMutexHeld: false));

            Assert.Contains(new CopyFile("state.json"), plan);
        });

    [Fact]
    public void Registry_steps_are_refused_under_the_test_host()
    {
        var before = RunValues();

        Assert.True(StartupRegistration.MigrateFromTaskSpaces(deleteOld: true, setNew: true).IsFailure);

        Assert.Equal(before, RunValues());
    }
}
