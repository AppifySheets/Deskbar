using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Claude;

// WHERE THE `claude` PROGRAM IS, asked fresh every time (task dbe25323, "can't start claude").
//
// Deskbar used to run a bare `claude` and leave the finding to Windows. Without a shell (ClaudeCli sets
// UseShellExecute = false) that finds only a claude.exe in a folder of the PATH Deskbar STARTED with, and a
// colleague's machine showed both ways that goes wrong: "An error occurred trying to start process 'claude' ...
// The system cannot find the file specified." Either Claude Code was installed after Deskbar started (the PATH
// Deskbar holds is the one from its own start, often the sign-in), or the native installer's folder,
// %USERPROFILE%\.local\bin, was never put on the PATH at all (the installer only prints a hint to add it), and the
// colleague's own terminal found claude some other way.
//
// So the search runs over three PATHs, in this order: Deskbar's own, then the user's and the machine's as the
// registry holds them NOW (Environment reads those from the registry on each call, with %VARS% expanded), so an
// install or PATH edit made while Deskbar runs is seen without a restart. ClaudeStart.ExecutableIn then adds the
// native installer's folder as the last place to look. A lookup is a few dozen File.Exists calls, cheap enough to
// make on every start, resume and stop, and it is never cached, because a miss can succeed a minute later
// ("never cache a lookup failure that can succeed later", CLAUDE.md's traps).
//
// Only claude.exe counts. An npm install puts a claude.cmd on the PATH, and a .cmd runs through cmd.exe, which
// re-parses its arguments: `claude --bg <message>` carries the task's own text, so a title with & or | in it would
// be read as a command. Deskbar does not run that, and says so in the not-found message instead.
public static class ClaudeLocator
{
    /// <summary>The full path of claude.exe, or None when Claude Code cannot be found.</summary>
    public static Maybe<string> Find() =>
        ClaudeStart.ExecutableIn(
            SearchPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists);

    /// <summary>
    /// Whether Windows Terminal's wt.exe can be started (ClaudeStart.HasWindowsTerminal), searched over the same three
    /// PATHs and asked fresh on every launch for the same reason: Windows Server 2022 has none (task abfe1db8), and one
    /// installed while Deskbar runs should be used from the next click on.
    /// </summary>
    public static bool HasWindowsTerminal() =>
        ClaudeStart.HasWindowsTerminal(
            SearchPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            File.Exists);

    /// <summary>
    /// What a failed lookup says, in the thread and to the user at once: where Deskbar looked and how to install
    /// Claude Code so it is found.
    /// </summary>
    public const string NotFound =
        "Claude Code is not installed where Deskbar can find it: there is no claude.exe on the PATH or in "
        + @"%USERPROFILE%\.local\bin." + "\n\n"
        + "Install it with Claude Code's native installer (in PowerShell: irm https://claude.ai/install.ps1 | iex), "
        + "then try again. An npm install is not enough, because it has no claude.exe.";

    // Deskbar's own PATH first, so a machine where claude was always found keeps finding the same one, then the
    // user's and the machine's from the registry. Repeats are harmless: the first folder holding claude.exe wins.
    static string SearchPath() =>
        string.Join(';', new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(target => Environment.GetEnvironmentVariable("PATH", target) ?? ""));
}
