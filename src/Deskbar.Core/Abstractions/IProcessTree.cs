using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Abstractions;

// Who started whom (#94). Petre: "if an app starts another app -- VS Code opening the browser via a
// clicked link -- the started app's window should be moved to the same workspace as the app that
// started it."
//
// One process at a time rather than a whole snapshot, because the walk up the chain stops as soon as
// it finds an answer: measured on Petre's machine, a browser window opened from inside VS Code is
// seven hops from the VS Code window, while an app launched from the taskbar is one hop from the
// shell and stops there. Enumerating every process on the machine to answer either would cost far
// more than the handful of lookups the walk actually makes.
public interface IProcessTree
{
    // None for a process that has gone, or one this app is not allowed to open.
    Maybe<ProcessFacts> Of(int processId);

    // The process's command line, or None when it cannot be read (gone, elevated, protected). Asked only for a
    // process named claude, to find a hidden Claude session's pty host (LaunchedBy.BackgroundSession). None by
    // default, which turns that tier off: a tree that cannot read command lines places nothing by session.
    Maybe<string> CommandLineOf(int processId) => Maybe<string>.None;
}
