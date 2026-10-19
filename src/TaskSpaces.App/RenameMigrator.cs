using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Migration;

namespace TaskSpaces.App;

// The Windows half of the rename migration: it gathers the facts RenameMigration.Plan decides on, and
// applies the steps that plan returns. Every rule lives in the plan (TaskSpaces.Core), so nothing here
// decides anything; it looks, and it does what it is told.
//
// Runs once per start, in App.OnStartup, after both single-instance mutexes are taken and before the
// state store is opened. See RenameMigration for the reasoning behind each rule.
static class RenameMigrator
{
    // The root is an argument rather than AppPaths.Root so the tests can hand in a temporary folder.
    // The app passes AppPaths.Root.
    //
    // RootOverridden is true under DESKBAR_APPDATA and ALSO for any root that is not the real %APPDATA%,
    // so a root handed in by a test counts as a rehearsal too and can never reach the real Run key, even
    // if the entry-assembly check beside it were ever wrong.
    public static RenameFacts Gather(string root, bool awaitingPrevious, bool oldMutexHeld)
    {
        var oldDir = AppPaths.OldStateDirUnder(root);
        return new RenameFacts(
            OldFolderExists: Directory.Exists(oldDir),
            NewFolderExists: Directory.Exists(AppPaths.NewStateDirUnder(root)),
            OldFilesPresent: [.. RenameMigration.StateFiles.Where(name => File.Exists(Path.Combine(oldDir, name)))],
            OldShimExists: File.Exists(Path.Combine(oldDir, AppPaths.OldShimName)),
            OldRunValueExists: StartupRegistration.LegacyIsSet,
            NewRunValueExists: StartupRegistration.IsEnabled,
            OldMutexHeld: oldMutexHeld,
            AwaitingPrevious: awaitingPrevious,
            RootOverridden: AppPaths.RootOverridden || !SamePath(root, AppPaths.RealRoot),
            IsTheApp: StartupRegistration.IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name));
    }

    static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    // A failure only for a refusal, carrying the message to show, and then NOTHING else is done, even if
    // the list holds other steps. Every other step is attempted, traced, and allowed to fail on its own:
    // a state file that could not be copied, a shim that could not be deleted or a Run value that could
    // not be swapped is no reason not to start, and the app starting with a fresh file beats a startup
    // that throws. The old folder is never written to except for the shim, and never deleted.
    public static Result Apply(string root, IReadOnlyList<RenameStep> steps, Action<string> trace)
    {
        if (steps.OfType<RefuseToStart>().FirstOrDefault() is { } refusal)
            return Result.Failure(refusal.Message);

        var oldDir = AppPaths.OldStateDirUnder(root);
        var newDir = AppPaths.NewStateDirUnder(root);

        // The new folder first, so the copies have somewhere to land. overwrite: false, because the plan
        // copies only while the new folder did not exist: a file already there was written by Deskbar and
        // is newer than anything in the old folder, and the copy failing loudly in the trace is right.
        var copies = steps.OfType<CopyFile>().ToList();
        if (copies.Count > 0)
            Attempt($"creating {newDir}", () => Directory.CreateDirectory(newDir), trace);
        copies.ForEach(copy => Attempt($"copying {copy.Name}",
            () => File.Copy(Path.Combine(oldDir, copy.Name), Path.Combine(newDir, copy.Name), overwrite: false), trace));

        if (steps.OfType<DeleteOldShim>().Any())
            Attempt($"deleting the old shim {AppPaths.OldShimName}",
                () => File.Delete(Path.Combine(oldDir, AppPaths.OldShimName)), trace);

        // One call for both registry steps, through StartupRegistration so the same OwnedByThisProcess
        // guard as Enable decides whether the Run key is touched at all.
        var deleteOld = steps.OfType<DeleteOldRunValue>().Any();
        var setNew = steps.OfType<SetNewRunValueToThisExe>().Any();
        if (deleteOld || setNew)
            StartupRegistration.MigrateFromTaskSpaces(deleteOld, setNew).Match(
                () => trace($"rename migration: start with Windows moved (old value deleted: {deleteOld}, new value set: {setNew})"),
                error => trace($"rename migration: {error}"));

        return Result.Success();
    }

    static void Attempt(string what, Action action, Action<string> trace) =>
        Result.Try(action, error => $"rename migration: {what} failed: {error.Message}")
            .Match(() => trace($"rename migration: {what}"), trace);

    // --- the handshake with the copy that started this one -----------------------------------------
    //
    // `--await-previous` used to mean "wait for the single-instance mutex", and that no longer works for
    // the handover from 1.x: the mutex names differ, and 1.x releases its own before it starts this exe,
    // so the new name is free at once and this copy would not wait at all. It would then copy the state
    // while 1.x's exit handler is still writing time.json. So the copy started with the switch waits for
    // the PROCESS that started it, its parent, to be gone, bounded so a parent that never exits costs
    // fifteen seconds rather than a start. A 2.x predecessor is waited for the same way, harmlessly: it
    // is on its way out too.
    //
    // Returns what happened, in words, for the trace.
    public static string AwaitParentExit(TimeSpan timeout) =>
        ParentProcessId().Match(
            parent => WaitForExit(parent, timeout),
            () => "parent wait: the parent process id could not be read, not waiting");

    static string WaitForExit(int parentId, TimeSpan timeout)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var self = Process.GetCurrentProcess();

            // A parent that died before this line may have had its id reused by an unrelated process,
            // which would then be waited on for nothing. A process started after this one cannot be its
            // parent.
            if (parent.StartTime > self.StartTime)
                return $"parent wait: {parentId} is a newer process reusing the parent's id, not waiting";

            return parent.WaitForExit(timeout)
                ? $"parent wait: {parentId} ({parent.ProcessName}) exited"
                : $"parent wait: {parentId} still running after {timeout.TotalSeconds:0} s, going on without it";
        }
        // Gone already, which is the wait over: GetProcessById throws ArgumentException for an id with no
        // process, and the other members InvalidOperationException once it has exited in between.
        catch (ArgumentException) { return $"parent wait: {parentId} already gone"; }
        catch (InvalidOperationException) { return $"parent wait: {parentId} already gone"; }
        // Not allowed to look at it. Nothing to wait on that can be seen, so go on rather than fail.
        catch (Win32Exception error) { return $"parent wait: {parentId} could not be read ({error.Message}), not waiting"; }
    }

    // ProcessBasicInformation (class 0) of this process. Only InheritedFromUniqueProcessId is read; the
    // other fields are declared so the struct's SIZE is right, which is what NtQueryInformationProcess
    // checks its length argument against. Same layout as ProcessCommandLine's in TaskSpaces.Windows.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    const int ProcessBasicInformationClass = 0;

    // GetCurrentProcess's pseudo-handle: always valid for this process, never closed.
    static readonly nint CurrentProcess = -1;

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(nint process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    static Maybe<int> ParentProcessId()
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(CurrentProcess, ProcessBasicInformationClass, ref info,
                   Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 && info.InheritedFromUniqueProcessId != 0
            ? Maybe.From((int)info.InheritedFromUniqueProcessId)
            : Maybe<int>.None;
    }
}
