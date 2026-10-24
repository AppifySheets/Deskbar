using System.IO;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Tests.Processes;

// The one way Deskbar starts a program for someone (`start`, `restart-app`, the dock's pins, Explorer,
// Task Manager). Unpackaged it is the shell's Process.Start as it always was; packaged it is cmd's
// `start` under CreateProcess with the breakaway attribute, so the program does not run as Deskbar's
// package. docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Both tests start only cmd.exe, which exits at once. The breakaway attribute is accepted from an
// unpackaged parent too, so the packaged path runs for real on this test host, attribute and all.
public class DetachedLauncherTests
{
    [Fact]
    public void Unpackaged_start_of_cmd_returns_a_pid()
    {
        var pid = DetachedLauncher.Start("cmd.exe", ["/c", "exit"], null, packaged: false);
        Assert.True(pid.IsSuccess);
        Assert.NotNull(pid.Value);
    }

    [Fact]
    public void Packaged_path_starts_cmd_with_breakaway_and_returns_null_pid()
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var result = DetachedLauncher.Start("cmd.exe", ["/c", $"echo x> \"{marker}\""], null, packaged: true);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
            Assert.Null(result.Value);
            SpinWait.SpinUntil(() => File.Exists(marker), 5000);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            // The inner cmd may still hold the file for a moment after it appears; a leftover temp file is
            // not worth failing a test over.
            try { File.Delete(marker); } catch (IOException) { }
        }
    }

    // A program that is not there: the unpackaged path fails the way Process.Start does, with its message.
    [Fact]
    public void Unpackaged_start_of_a_missing_program_is_a_failure()
    {
        var result = DetachedLauncher.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"), [], null, packaged: false);
        Assert.True(result.IsFailure);
    }
}
