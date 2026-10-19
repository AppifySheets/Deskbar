using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Migration;

namespace TaskSpaces.App;

// What RenameMigrator.Apply can come back with instead of success, each carrying the message to show.
// Two types rather than one message because the app does opposite things with them: a refusal ends
// the start, a state file that did not come across is a warning and the start goes on.
abstract record RenameProblem(string Message);

sealed record Refused(string Message) : RenameProblem(Message);

sealed record StateNotCarried(string Message) : RenameProblem(Message);

// How RenameMigrator.AwaitParentExit's wait ended. NotVisible: the process exists but this one is not
// allowed to look at it, so there is nothing to wait on.
enum ParentWait { Exited, AlreadyGone, TimedOut, NotVisible }

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
            // The new state.json, not the new folder: the folder can exist with only trace.on in it.
            NewStateFileExists: File.Exists(Path.Combine(AppPaths.NewStateDirUnder(root), RenameMigration.StateFile)),
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

    // Two kinds of failure, and the caller treats them differently (App.OnStartup):
    //
    // - Refused: the plan said not to start. NOTHING else is done, even if the list holds other steps,
    //   and the app shows the message and exits.
    // - StateNotCarried: state.json, the workspaces, could not be copied. Every other step has still
    //   been attempted, and the app shows the message as a warning and STARTS: a fresh file beats a
    //   startup that throws, but starting with no workspaces must not happen silently, because once the
    //   app has saved a state.json of its own nothing tries the copy again.
    //
    // Every other failure is traced and nothing more: a time.json or last-used.json that could not be
    // copied, a shim that could not be deleted or a Run value that could not be swapped is no reason to
    // stop or to interrupt anyone. The old folder is never written to except for the shim, and never
    // deleted.
    public static UnitResult<RenameProblem> Apply(string root, IReadOnlyList<RenameStep> steps, Action<string> trace)
    {
        if (steps.OfType<RefuseToStart>().FirstOrDefault() is { } refusal)
            return UnitResult.Failure<RenameProblem>(new Refused(refusal.Message));

        var oldDir = AppPaths.OldStateDirUnder(root);
        var newDir = AppPaths.NewStateDirUnder(root);

        // The new folder first, so the copies have somewhere to land (it may exist already, holding only
        // trace.on). overwrite: false, because the plan copies only while the new state.json does not
        // exist, and any other file already there was written by Deskbar and is newer than anything in
        // the old folder: that copy failing in the trace is right.
        var copies = steps.OfType<CopyFile>().ToList();
        if (copies.Count > 0)
            Attempt($"creating {newDir}", () => Directory.CreateDirectory(newDir), trace);
        var stateNotCopied = copies
            .Select(copy => (copy.Name, Copied: Attempt($"copying {copy.Name}",
                () => File.Copy(Path.Combine(oldDir, copy.Name), Path.Combine(newDir, copy.Name), overwrite: false), trace)))
            .ToList()
            .Any(copy => copy.Name == RenameMigration.StateFile && copy.Copied.IsFailure);

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

        return stateNotCopied
            ? UnitResult.Failure<RenameProblem>(new StateNotCarried(StateNotCarriedMessage(oldDir, newDir)))
            : UnitResult.Success<RenameProblem>();
    }

    // Says what happened, where the workspaces still are, and how to bring them across by hand. The
    // folder is named in full rather than as %APPDATA%\TaskSpaces so it is right under DESKBAR_APPDATA
    // too, and can be pasted into Explorer as it stands.
    static string StateNotCarriedMessage(string oldDir, string newDir) =>
        $"Deskbar could not copy your workspaces from TaskSpaces, so it is starting without them.\n\n" +
        $"They are still in {oldDir}. To bring them across, exit Deskbar from its tray icon, copy " +
        $"{RenameMigration.StateFile} from that folder into {newDir}, and start Deskbar again.";

    // The step's outcome is returned as well as traced, so Apply can tell a state.json that did not
    // copy from everything else.
    static Result Attempt(string what, Action action, Action<string> trace) =>
        Result.Try(action, error => $"rename migration: {what} failed: {error.Message}")
            .Tap(() => trace($"rename migration: {what}"))
            .TapError(trace);

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
    // Split in two so the wait can be tested on a process the test starts itself: OnStartup resolves the
    // parent's id (ParentProcessId) and hands it to AwaitParentExit. Each traces what it found, and the
    // wait also returns it, which is what the tests read.
    public static ParentWait AwaitParentExit(int parentPid, TimeSpan timeout, Action<string> trace)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);

            // Named BEFORE the wait. Once the process has exited ProcessName throws
            // InvalidOperationException, so read afterwards it sent every successful wait into the
            // "already gone" catch below and the "exited" line could never be written.
            var name = parent.ProcessName;
            var exited = parent.WaitForExit(timeout);
            trace(exited
                ? $"parent wait: {parentPid} ({name}) exited"
                : $"parent wait: {parentPid} still running after {timeout.TotalSeconds:0} s, going on without it");
            return exited ? ParentWait.Exited : ParentWait.TimedOut;
        }
        // Gone already, which is the wait over: GetProcessById throws ArgumentException for an id with no
        // process, and the other members InvalidOperationException once it has exited in between.
        catch (ArgumentException) { return AlreadyGone(parentPid, trace); }
        catch (InvalidOperationException) { return AlreadyGone(parentPid, trace); }
        // Not allowed to look at it. Nothing to wait on that can be seen, so go on rather than fail.
        catch (Win32Exception error)
        {
            trace($"parent wait: {parentPid} could not be read ({error.Message}), not waiting");
            return ParentWait.NotVisible;
        }
    }

    static ParentWait AlreadyGone(int parentPid, Action<string> trace)
    {
        trace($"parent wait: {parentPid} already gone");
        return ParentWait.AlreadyGone;
    }

    // The id of the process that started this one, when there is one worth waiting for. None, traced,
    // when the id cannot be read, and when it now names a process NEWER than this one: a parent that
    // died before this line may have had its id reused by an unrelated process, which would then be
    // waited on for nothing, and a process started after this one cannot be its parent. (The check lives
    // here rather than in the wait so the wait can be tested against a child the test starts, which is
    // necessarily newer than the test.)
    public static Maybe<int> ParentProcessId(Action<string> trace)
    {
        var parentId = ReadParentId();
        if (parentId.HasNoValue)
        {
            trace("parent wait: the parent process id could not be read, not waiting");
            return Maybe<int>.None;
        }

        try
        {
            using var parent = Process.GetProcessById(parentId.Value);
            using var self = Process.GetCurrentProcess();
            if (parent.StartTime <= self.StartTime)
                return parentId;

            trace($"parent wait: {parentId.Value} is a newer process reusing the parent's id, not waiting");
            return Maybe<int>.None;
        }
        catch (ArgumentException) { AlreadyGone(parentId.Value, trace); return Maybe<int>.None; }
        catch (InvalidOperationException) { AlreadyGone(parentId.Value, trace); return Maybe<int>.None; }
        catch (Win32Exception error)
        {
            trace($"parent wait: {parentId.Value} could not be read ({error.Message}), not waiting");
            return Maybe<int>.None;
        }
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

    static Maybe<int> ReadParentId()
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(CurrentProcess, ProcessBasicInformationClass, ref info,
                   Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 && info.InheritedFromUniqueProcessId != 0
            ? Maybe.From((int)info.InheritedFromUniqueProcessId)
            : Maybe<int>.None;
    }
}
