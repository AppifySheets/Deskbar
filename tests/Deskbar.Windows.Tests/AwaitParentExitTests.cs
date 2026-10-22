using System.Diagnostics;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The wait a copy started with --await-previous does before the rename migration: for the process
// that started it (TaskSpaces 1.x, or an older Deskbar) to be gone, so the state is not copied while
// 1.x's exit handler is still writing time.json. OnStartup resolves the parent's id and hands it in;
// here the "parent" is a ping.exe the test starts, which is real process exit without the app.
public class AwaitParentExitTests
{
    // ping waits about a second between echoes, so -n N lives for roughly N-1 seconds. No window, and
    // nothing that reads the console, so it behaves the same under a test runner.
    static Process Ping(int echoes) =>
        Process.Start(new ProcessStartInfo("ping.exe", $"-n {echoes} 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

    // The "exited" line names the process, and naming it has to happen BEFORE the wait: once it has
    // exited, ProcessName throws, and the outcome used to land in the "already gone" catch instead.
    [Fact]
    public void A_parent_that_exits_during_the_wait_is_waited_for()
    {
        using var parent = Ping(echoes: 2);
        var traced = new List<string>();

        var outcome = RenameMigrator.AwaitParentExit(parent.Id, TimeSpan.FromSeconds(15), traced.Add);

        Assert.Equal(ParentWait.Exited, outcome);
        Assert.True(parent.HasExited);
        Assert.Contains(traced, line => line.Contains("PING", StringComparison.OrdinalIgnoreCase) && line.Contains("exited"));
    }

    // No process has this id: Windows ids are multiples of four, and this one is not, and is far above
    // any id in use. The wait is over before it starts.
    [Fact]
    public void A_parent_that_is_already_gone_is_not_waited_for()
    {
        var clock = Stopwatch.StartNew();

        var outcome = RenameMigrator.AwaitParentExit(int.MaxValue, TimeSpan.FromSeconds(15), _ => { });

        Assert.Equal(ParentWait.AlreadyGone, outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    // A parent that never leaves costs the timeout and no more, and the start goes on without it.
    [Fact]
    public void A_parent_that_outlives_the_timeout_is_given_up_on()
    {
        using var parent = Ping(echoes: 30);
        try
        {
            var clock = Stopwatch.StartNew();

            var outcome = RenameMigrator.AwaitParentExit(parent.Id, TimeSpan.FromSeconds(1), _ => { });

            Assert.Equal(ParentWait.TimedOut, outcome);
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
            Assert.False(parent.HasExited);
        }
        finally
        {
            parent.Kill(entireProcessTree: true);
            parent.WaitForExit();
        }
    }
}
