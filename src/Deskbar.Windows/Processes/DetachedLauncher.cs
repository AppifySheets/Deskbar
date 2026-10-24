using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Windows.Packaging;

namespace Deskbar.Windows.Processes;

// THE ONE WAY Deskbar starts a program on someone's behalf: `deskbar.cmd start` and `restart-app`
// (ProgramStarter), the dock's pins and their "Open file location" (TaskbarPins), the debug dump's Explorer
// window (DebugDump.Reveal) and the stats line's Task Manager (FloatingBar.Stats).
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// WHY IT EXISTS: a process started by a packaged process inherits its package identity unless the parent
// breaks it away. Under the Store copy, VS Code started by `restart-app` would otherwise run as Deskbar's
// package and be ended along with it whenever the Store updates or removes Deskbar. Breaking away is a
// CreateProcess attribute, and the shell's Process.Start (UseShellExecute) has no way to pass one.
//
//   Unpackaged (the loose exe): exactly what every call site did before, Process.Start through the shell with
//   the arguments one by one in ArgumentList. The answer is the new process's id, or null when the shell
//   handed the request to a copy already running.
//
//   Packaged (the Store copy): CreateProcess of %SystemRoot%\System32\cmd.exe with no window, carrying
//   PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE.
//   Microsoft documents that flag as acting on the CHILDREN of the process it is given to: cmd itself still
//   runs inside Deskbar's package, and the processes cmd creates are created outside it. That is one reason
//   for the cmd hop: the program the user wanted is cmd's child, so it is the one that breaks away. The other
//   is `start`, which finds a bare program name through the PATH and App Paths the way the shell does (plain
//   CreateProcess cannot, and a `code` from `deskbar.cmd start` must still work) and honours a program's
//   request to run elevated (Task Manager). cmd itself exits as soon as `start` has handed the program off. The
//   line is CommandLineQuoting.ForCmdStart's. The answer is always null: `start` hands the program off and
//   cmd exits, so the only pid in hand is cmd's, which is useless, and every caller already treats null as
//   "expect it by name only".
//
// What the packaged path cannot report: cmd started but `start` failed (no such program). CreateProcess has
// succeeded by then, so that is a success here, and `start` itself tells the user. Waiting for cmd's exit
// code instead would block the UI thread on whatever `start` waits for, a UAC prompt included.
public static class DetachedLauncher
{
    // The switch on the package: breakaway under the Store copy, the shell as always outside it.
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
    public static Result<int?> Start(string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        Start(program, arguments, workingDirectory, PackageIdentity.IsPackaged);

    // The same with the package question answered by the caller, so the tests can run both paths on an
    // unpackaged test host. The error is the bare reason; each call site words its own message around it.
    internal static Result<int?> Start(string program, IReadOnlyList<string> arguments, string? workingDirectory, bool packaged) =>
        packaged ? StartBrokenAway(program, arguments, workingDirectory) : StartThroughShell(program, arguments, workingDirectory);

    static Result<int?> StartThroughShell(string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        Result.Try(() =>
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDirectory ?? "",
            };
            arguments.ToList().ForEach(info.ArgumentList.Add);
            using var started = Process.Start(info);
            return started?.Id;
        }, e => e.Message);

    static Result<int?> StartBrokenAway(string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        Result.Try(() =>
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // cmd skips its own name (the first token) before it reads /d /c, so the line starts with it.
            var commandLine = new StringBuilder($"\"{cmd}\" {CommandLineQuoting.ForCmdStart(program, arguments, workingDirectory)}");
            WithBreakawayAttribute(attributes =>
            {
                var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = attributes };
                if (!CreateProcessW(cmd, commandLine, 0, 0, false, ExtendedStartupInfoPresent | CreateNoWindow, 0, null, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            });
            return (int?)null;
        }, e => e.Message);

    // A one-entry attribute list holding the breakaway policy, alive for the duration of `use` and freed after.
    // The value is a DWORD in unmanaged memory because the list only points at it: it must stay put until
    // CreateProcess has read it.
    static void WithBreakawayAttribute(Action<nint> use)
    {
        var size = (nint)0;
        // The size probe: fails by design (ERROR_INSUFFICIENT_BUFFER) and writes the size needed.
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var policy = Marshal.AllocHGlobal(sizeof(uint));
        var initialised = false;
        try
        {
            Marshal.WriteInt32(policy, (int)DesktopAppBreakawayEnableProcessTree);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialised = true;
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeDesktopAppPolicy, policy, sizeof(uint), 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            use(list);
        }
        finally
        {
            if (initialised) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(list);
        }
    }

    // ProcThreadAttributeValue(18, thread: false, input: true, additive: false): attribute number 18 with the
    // input bit (0x20000). Not in the .NET headers, so spelled out from winbase.h.
    const nint ProcThreadAttributeDesktopAppPolicy = 0x00020012;
    const uint DesktopAppBreakawayEnableProcessTree = 0x01;
    const uint ExtendedStartupInfoPresent = 0x00080000;
    const uint CreateNoWindow = 0x08000000;

    // STARTUPINFOW. The string fields are pointers left at zero, which keeps the struct blittable.
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    // STARTUPINFOEXW: the plain one followed by the attribute list. Its size goes in StartupInfo.Size, which
    // together with EXTENDED_STARTUPINFO_PRESENT is how CreateProcess knows to look for the list.
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    // lpCommandLine is a StringBuilder because CreateProcessW may write into it, so it must not be a string.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(nint attributeList, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CloseHandle(nint handle);
}
