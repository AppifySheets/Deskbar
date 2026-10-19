using System.IO;

namespace TaskSpaces.App;

// Where the app keeps its files: %APPDATA%\Deskbar, plus the folder 1.x used, %APPDATA%\TaskSpaces,
// which the rename migration copies from and never writes to. One place, because three call sites
// (the state folder, the trace marker, the control shim) used to build the path by hand, and the
// override below has to reach every one of them or it protects nothing.
//
// DESKBAR_APPDATA, when set and not blank, replaces %APPDATA% as the root of BOTH folders. It exists
// for one situation: a Debug 2.0 build on a machine that still runs 1.x every day (Petre's, until the
// rename is on main). Started against the real %APPDATA%, that build's first start would migrate the
// real profile, and the 1.x builds from main would stop seeing anything that changes after it. With
// the override, the old folder and the new one alike resolve under a scratch root, so the migration
// can be rehearsed by copying the real TaskSpaces folder there. In that mode the migration also
// touches no registry value (it reads RootOverridden), so the real autostart entry is never swapped
// by a build that is only being tried out.
//
// Read once, when the type is first touched: a running process's environment does not change under
// it, and every caller has to agree on one root for the whole run.
static class AppPaths
{
    const string OverrideVariable = "DESKBAR_APPDATA";

    // Declared before the properties on purpose: static initializers run in textual order, and the
    // three below are computed from this one.
    //
    // Made absolute here, once. A relative value would otherwise resolve against whatever the current
    // directory happens to be, and for a copy started by the Run key at login that is System32: the
    // scratch root meant to protect the real profile would land among Windows' own files instead.
    static readonly string? overrideRoot =
        Environment.GetEnvironmentVariable(OverrideVariable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value.Trim())
            : null;

    public static bool RootOverridden { get; } = overrideRoot is not null;

    // The real %APPDATA%, whatever the override says. The rename migration compares against it, so a
    // root handed to it that is anything else is treated as a rehearsal and kept out of the registry.
    public static string RealRoot { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Root { get; } = overrideRoot ?? RealRoot;

    // state.json, time.json, last-used.json, the control shim and the trace marker all live here.
    public static string NewStateDir { get; } = NewStateDirUnder(Root);

    // The 1.x folder, kept by its old name deliberately: the migration copies from it once and leaves
    // it as it is, so a rollback to 1.x finds its files where it left them.
    public static string OldStateDir { get; } = OldStateDirUnder(Root);

    // The same two folders under a root given explicitly. The rename migration takes its root as an
    // argument through these, so its tests can point it at a temporary folder without setting
    // DESKBAR_APPDATA, which is read once per process and would fix the root for every other test.
    public static string NewStateDirUnder(string root) => Path.Combine(root, "Deskbar");

    public static string OldStateDirUnder(string root) => Path.Combine(root, "TaskSpaces");

    // The control shim 1.x wrote into its folder. The migration deletes it so it cannot start the old exe.
    public const string OldShimName = "taskspaces.cmd";
}
