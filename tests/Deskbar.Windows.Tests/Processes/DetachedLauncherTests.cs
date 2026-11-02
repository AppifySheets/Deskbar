using System.IO;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Tests.Processes;

// The one way Deskbar starts a program for someone (`start`, `restart-app`, the dock's pins, Explorer,
// Task Manager). Unpackaged it is the shell's Process.Start as it always was; packaged it is cmd's
// `start` under CreateProcess with the breakaway attribute, so the program does not run as Deskbar's
// package. docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// The program started is wscript.exe running a two-line JScript that creates a marker file named by its
// argument, so each test proves the program really ran and got its argument. wscript rather than cmd
// because it has no window: `start` gives a console program a console of its own, and these tests run in
// the routine suite on a machine somebody is working at, many times a day. //B keeps any script error
// silent as well. The breakaway attribute is accepted from an unpackaged parent too, so the packaged path
// runs for real on this test host, attribute and all.
public sealed class DetachedLauncherTests : IDisposable
{
    readonly string script = Path.Combine(Path.GetTempPath(), $"deskbar-launcher-{Guid.NewGuid():N}.js");
    readonly string marker = Path.Combine(Path.GetTempPath(), $"deskbar-launcher-{Guid.NewGuid():N}.txt");

    public DetachedLauncherTests() =>
        File.WriteAllText(script, "new ActiveXObject(\"Scripting.FileSystemObject\").CreateTextFile(WScript.Arguments(0)).Close();");

    // wscript's own switches, then the script, then the marker as the script's one argument.
    IReadOnlyList<string> Arguments => ["//B", "//E:JScript", script, marker];

    [Fact]
    public void Unpackaged_start_returns_a_pid_and_the_program_runs()
    {
        var pid = DetachedLauncher.Start("wscript.exe", Arguments, null, packaged: false);
        Assert.True(pid.IsSuccess, pid.IsFailure ? pid.Error : "");
        Assert.NotNull(pid.Value);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), 5000), "the started program never wrote its marker");
    }

    [Fact]
    public void Packaged_path_starts_the_program_with_breakaway_and_returns_null_pid()
    {
        var result = DetachedLauncher.Start("wscript.exe", Arguments, null, packaged: true);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Null(result.Value);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), 5000), "the started program never wrote its marker");
    }

    // The verbatim form (Explorer's `/select,"<path>"`) on both paths, run for real: the marker's path has a
    // comma and a space in it and is quoted once by the caller, and the script only finds it if neither path
    // quoted it again or split it at the comma or the space.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verbatim_arguments_reach_the_program_as_written(bool packaged)
    {
        var awkward = Path.Combine(Path.GetTempPath(), $"deskbar launcher, {Guid.NewGuid():N}.txt");
        try
        {
            var result = DetachedLauncher.StartVerbatim("wscript.exe", $"//B //E:JScript \"{script}\" \"{awkward}\"", null, packaged);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(awkward), 5000), "the started program never wrote its marker");
        }
        finally
        {
            try { File.Delete(awkward); } catch (IOException) { }
        }
    }

    // A program that is not there: the unpackaged path fails the way Process.Start does, with its message.
    [Fact]
    public void Unpackaged_start_of_a_missing_program_is_a_failure()
    {
        var result = DetachedLauncher.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"), [], null, packaged: false);
        Assert.True(result.IsFailure);
    }

    // RunOutsidePackage waits: the folder exists the moment it returns, with no spin. The path has a space and
    // a comma in it, so the line's quoting (the extra pair cmd /c strips) is exercised for real.
    [Fact]
    public void Run_outside_package_waits_for_the_program_and_its_work_is_done()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"deskbar run outside, {Guid.NewGuid():N}");
        try
        {
            var result = DetachedLauncher.RunOutsidePackage(Cmd, ["/d", "/c", "mkdir", folder], TimeSpan.FromSeconds(10));
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            try { Directory.Delete(folder); } catch (IOException) { }
        }
    }

    // A non-zero exit code is a failure naming the program and the code.
    [Fact]
    public void Run_outside_package_reports_a_nonzero_exit_code()
    {
        var result = DetachedLauncher.RunOutsidePackage(Cmd, ["/d", "/c", "exit", "3"], TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailure);
        Assert.Equal("cmd.exe exited with code 3", result.Error);
    }

    static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    // The started program may still hold its file for a moment after it appears; a leftover temp file is not
    // worth failing a test over.
    public void Dispose() =>
        new[] { script, marker }.ToList().ForEach(path =>
        {
            try { File.Delete(path); } catch (IOException) { }
        });
}
