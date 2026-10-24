using System.IO;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The batch file the running app leaves at %APPDATA%\Deskbar\deskbar.cmd so other programs have
// one fixed path to call, whatever the exe is named this version. Measured before it was written:
// PowerShell does not wait for a GUI exe called directly (nothing captured, $LASTEXITCODE empty), and a
// batch file does, with output and exit code intact. See ControlCommandLine.WriteShim.
public class ControlShimTests
{
    [Fact]
    public void The_shim_runs_the_exe_with_every_argument_and_hands_back_its_exit_code()
    {
        var content = ControlCommandLine.ShimContent(@"C:\Tools\TaskSpaces-1.17.0-win-x64.exe");

        Assert.Contains("\"C:\\Tools\\TaskSpaces-1.17.0-win-x64.exe\" ctl %*", content);
        Assert.Contains("exit /b %ERRORLEVEL%", content);
    }

    // cmd expands %NAME% even inside quotes, so a literal percent in the path has to be doubled.
    [Fact]
    public void A_percent_sign_in_the_exe_path_survives_cmd()
    {
        var content = ControlCommandLine.ShimContent(@"C:\100% real\TaskSpaces.exe");

        Assert.Contains("\"C:\\100%% real\\TaskSpaces.exe\" ctl %*", content);
    }

    // Under the Store package the shim points at the execution alias, which runs deskbar-cli.exe: a
    // console client that takes the command directly, so there is no `ctl` before the arguments.
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
    [Fact]
    public void Alias_shim_forwards_without_ctl() =>
        Assert.Contains("\"C:\\u\\WindowsApps\\deskbar.exe\" %*", ControlCommandLine.ShimContent(@"C:\u\WindowsApps\deskbar.exe", aliasTarget: true));

    [Fact]
    public void Alias_shim_keeps_the_header_and_the_exit_code()
    {
        var content = ControlCommandLine.ShimContent(@"C:\u\WindowsApps\deskbar.exe", aliasTarget: true);

        Assert.DoesNotContain(" ctl ", content);
        Assert.StartsWith("@rem Written by Deskbar at startup", content);
        Assert.Contains("exit /b %ERRORLEVEL%", content);
    }

    [Fact]
    public void An_exe_target_still_forwards_through_ctl() =>
        Assert.Equal(ControlCommandLine.ShimContent(@"C:\Tools\Deskbar.exe"),
            ControlCommandLine.ShimContent(@"C:\Tools\Deskbar.exe", aliasTarget: false));

    [Fact]
    public void Writing_the_alias_shim_writes_no_ctl()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ts-shim-{Guid.NewGuid():N}");
        try
        {
            ControlCommandLine.WriteShim(folder, @"C:\u\WindowsApps\deskbar.exe", aliasTarget: true);

            Assert.Contains("\"C:\\u\\WindowsApps\\deskbar.exe\" %*", File.ReadAllText(Path.Combine(folder, "deskbar.cmd")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Writing_the_shim_creates_it_and_rewrites_it_for_a_new_exe()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ts-shim-{Guid.NewGuid():N}");
        try
        {
            ControlCommandLine.WriteShim(folder, @"C:\old\TaskSpaces.exe");
            ControlCommandLine.WriteShim(folder, @"C:\new\TaskSpaces.exe");

            var written = File.ReadAllText(Path.Combine(folder, "deskbar.cmd"));
            Assert.Contains(@"C:\new\TaskSpaces.exe", written);
            Assert.DoesNotContain(@"C:\old\TaskSpaces.exe", written);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
