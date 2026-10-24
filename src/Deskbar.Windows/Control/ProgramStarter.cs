using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Control;

// Starts a program for `deskbar.cmd start` (RemoteControl.Start) and `restart-app`, and only for those: Deskbar
// starts nothing on its own. Petre: "can we start there, not move?", "yes, let's do it".
//
// Found the way Run and a terminal find it: a bare `code` resolves to code.cmd on the PATH, `chrome` through
// App Paths, a full path as given. The child inherits this process's environment, which the app cleaned of its
// own development variables at startup (LaunchEnvironment.Clean), the same as anything the bar's pins start.
//
// The arguments go one by one, so a folder with spaces arrives as one argument without the caller having to
// quote it twice through the pipe.
//
// The start itself is DetachedLauncher's: the shell as before for the loose exe, and under the Store copy a
// start that breaks away from Deskbar's package, so the program is not ended when the Store services Deskbar.
public static class ProgramStarter
{
    // The new process's id, or null when there is none to give: the shell handed the request to a copy already
    // running or opened a document in one, or the Store copy started it through cmd's `start`. The caller treats
    // null as "expect it by name only".
    public static Result<int?> Start(ProgramLaunch launch) =>
        DetachedLauncher.Start(launch.Program, launch.Arguments, launch.WorkingDirectory)
            .MapError(e => $"Could not start {launch.Program}: {e}");
}
