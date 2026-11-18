using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Shell;

namespace Deskbar.Windows.Tests;

// The applier that holds Windows' "Show flashing on taskbar apps" off while Deskbar runs
// (docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md). The rules are TaskbarFlashing.Plan's and
// are tested there; what can break here is the marker file, the order of the two writes, and the guard that keeps
// a test host away from the real setting. The registry is one variable, the state folder a scratch one.
public sealed class TaskbarFlashingSwitchTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-flashing-" + Guid.NewGuid().ToString("N"));
    readonly List<string> traced = [];
    bool flashing = true;
    bool writeFails;

    public TaskbarFlashingSwitchTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Marker => Path.Combine(dir, TaskbarFlashing.MarkerName);

    Result Write(bool on)
    {
        if (writeFails) return Result.Failure("access denied");
        flashing = on;
        return Result.Success();
    }

    void Sync(bool wanted) => TaskbarFlashingSwitch.Sync(wanted, dir, () => flashing, Write, traced.Add);

    [Fact]
    public void Taking_hold_remembers_on_and_turns_flashing_off()
    {
        Sync(true);

        Assert.Equal("1", File.ReadAllText(Marker));
        Assert.False(flashing);
    }

    [Fact]
    public void Taking_hold_of_a_switch_already_off_remembers_off()
    {
        flashing = false;

        Sync(true);

        Assert.Equal("0", File.ReadAllText(Marker));
        Assert.False(flashing);
    }

    // The start after a crash, and every later state pulse: the marker already holds the original.
    [Fact]
    public void A_second_sync_keeps_the_first_marker()
    {
        Sync(true);
        Sync(true);

        Assert.Equal("1", File.ReadAllText(Marker));
        Assert.False(flashing);
    }

    [Fact]
    public void Letting_go_turns_flashing_back_on_and_deletes_the_marker()
    {
        Sync(true);

        Sync(false);

        Assert.True(flashing);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void Letting_go_leaves_it_off_for_someone_who_had_it_off()
    {
        flashing = false;
        Sync(true);

        Sync(false);

        Assert.False(flashing);
        Assert.False(File.Exists(Marker));
    }

    // The marker must outlive a failed restore, or nothing would remember that flashing had been on.
    [Fact]
    public void A_failed_restore_keeps_the_marker_and_is_traced_not_thrown()
    {
        Sync(true);
        writeFails = true;

        Sync(false);

        Assert.True(File.Exists(Marker));
        Assert.Contains(traced, line => line.Contains("access denied"));

        writeFails = false;
        Sync(false);

        Assert.True(flashing);
        Assert.False(File.Exists(Marker));
    }

    // On is Windows' default, so a marker nobody can read restores to on.
    [Fact]
    public void A_marker_with_unreadable_content_restores_to_on()
    {
        flashing = false;
        File.WriteAllText(Marker, "garbage");

        Sync(false);

        Assert.True(flashing);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void Not_wanted_and_never_held_touches_nothing()
    {
        Sync(false);

        Assert.True(flashing);
        Assert.False(File.Exists(Marker));
        Assert.Empty(traced);
    }

    // The entry point the app calls. From a test host it must do nothing at all: this run would otherwise switch
    // off flashing on the machine of whoever runs the suite and drop a marker into their real state folder.
    [Fact]
    public void The_real_entry_point_does_nothing_from_a_test_host()
    {
        var before = TaskbarFlashingSwitch.FlashingOn;
        var marker = Path.Combine(AppPaths.NewStateDir, TaskbarFlashing.MarkerName);
        var markerBefore = File.Exists(marker);

        TaskbarFlashingSwitch.Sync(!before);
        TaskbarFlashingSwitch.Sync(true);

        Assert.Equal(before, TaskbarFlashingSwitch.FlashingOn);
        Assert.Equal(markerBefore, File.Exists(marker));
    }
}
