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
        AssertMarkerAppears(marker, pid.Value);
    }

    [Fact]
    public void Packaged_path_starts_the_program_with_breakaway_and_returns_null_pid()
    {
        var result = DetachedLauncher.Start("wscript.exe", Arguments, null, packaged: true);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Null(result.Value);
        AssertMarkerAppears(marker, null);
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
            AssertMarkerAppears(awkward, null);
        }
        finally
        {
            try { File.Delete(awkward); } catch (IOException) { }
        }
    }

    // StartConsole (task abfe1db8, the window on a Claude session without Windows Terminal), run for real: the program
    // gets the added variable AND keeps the inherited environment (SystemRoot stands for it), starts in the folder given,
    // and the answer is its pid. wscript again, so no console window opens on the machine the suite runs on: a GUI
    // program ignores CREATE_NEW_CONSOLE. The title cannot be seen without a console; StartupInfo carries it.
    [Fact]
    public void Start_console_passes_the_added_variable_the_inherited_environment_and_the_folder()
    {
        var reader = Path.Combine(Path.GetTempPath(), $"deskbar-console-{Guid.NewGuid():N}.js");
        File.WriteAllText(reader,
            "var sh = new ActiveXObject(\"WScript.Shell\"); var env = sh.Environment(\"PROCESS\");"
            + " var f = new ActiveXObject(\"Scripting.FileSystemObject\").CreateTextFile(WScript.Arguments(0));"
            + " f.Write(env(\"DESKBAR_CONSOLE_PROBE\") + \"|\" + env(\"SystemRoot\") + \"|\" + sh.CurrentDirectory); f.Close();");
        var folder = Path.GetTempPath().TrimEnd('\\');
        try
        {
            var result = DetachedLauncher.StartConsole($"\"wscript.exe\" //B //E:JScript \"{reader}\" \"{marker}\"", folder, "Probe · Claude",
                new Dictionary<string, string> { ["DESKBAR_CONSOLE_PROBE"] = "1" });

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
            Assert.NotNull(result.Value);
            AssertMarkerAppears(marker, result.Value);
            // The script may still be writing when the file first exists.
            Assert.True(SpinWait.SpinUntil(() => TryRead(marker).Length > 0, MarkerBound));
            Assert.Equal($"1|{Environment.GetEnvironmentVariable("SystemRoot")}|{folder}", TryRead(marker), ignoreCase: true);
        }
        finally
        {
            try { File.Delete(reader); } catch (IOException) { }
        }
    }

    static string TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
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

    // The bound is generous on purpose: SpinUntil returns the moment the file appears, so a run that works costs
    // the same whatever the bound is. On the GitHub runner the first wscript of a run takes 350 to 950 ms and the
    // rest about 60 ms, but one Release run (actions/runs/44544956567, attempt 1) missed the old 5 s bound on two
    // tests in a row and passed on the retry: the runner stalled for seconds, most likely Defender scanning the
    // freshly written script, which no test can control. 30 s still catches a launcher that never starts anything.
    static readonly TimeSpan MarkerBound = TimeSpan.FromSeconds(30);

    // Waits for the started program's marker. If it never comes, the message says how long was waited and, when
    // the launcher returned a pid, whether that process is still running or how it ended, so the next failure names
    // its cause (stuck, crashed, or never wrote) instead of only saying the file is missing.
    static void AssertMarkerAppears(string path, int? pid)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (SpinWait.SpinUntil(() => File.Exists(path), MarkerBound)) return;
        Assert.Fail($"the started program never wrote its marker within {clock.Elapsed.TotalSeconds:F1}s; {ProcessState(pid)}");
    }

    static string ProcessState(int? pid)
    {
        if (pid is not { } id) return "no pid to ask about (packaged path)";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return process.HasExited ? $"pid {id} exited with code {process.ExitCode}" : $"pid {id} is still running";
        }
        catch (ArgumentException)
        {
            return $"pid {id} is gone and its exit code is not readable";
        }
        catch (InvalidOperationException e)
        {
            return $"pid {id}: {e.Message}";
        }
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
