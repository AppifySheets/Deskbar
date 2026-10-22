using System.Diagnostics;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Windows.Control;

// Starts a program for `deskbar.cmd start` (RemoteControl.Start), and only for that: Deskbar starts nothing
// on its own. Petre: "can we start there, not move?", "yes, let's do it".
//
// Through the SHELL (UseShellExecute), so the program is found the way Run and a terminal find it: a bare
// `code` resolves to code.cmd on the PATH, `chrome` through App Paths, a full path as given. The child inherits
// this process's environment, which the app cleaned of its own development variables at startup
// (LaunchEnvironment.Clean), the same as anything the bar's pins start.
//
// The arguments go through ArgumentList one by one, so a folder with spaces arrives as one argument without
// the caller having to quote it twice through the pipe.
public static class ProgramStarter
{
    // The new process's id, or null when the shell started nothing new (it handed the request to a copy already
    // running, or opened a document in one), which the caller treats as "expect it by name only".
    public static Result<int?> Start(ProgramLaunch launch) =>
        Result.Try(() =>
        {
            var info = new ProcessStartInfo(launch.Program)
            {
                UseShellExecute = true,
                WorkingDirectory = launch.WorkingDirectory ?? "",
            };
            launch.Arguments.ToList().ForEach(info.ArgumentList.Add);
            using var started = Process.Start(info);
            return started?.Id;
        }, e => $"Could not start {launch.Program}: {e.Message}");
}
