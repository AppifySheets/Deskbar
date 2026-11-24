<#
.SYNOPSIS
  Lists every Win32 desktop in your session that some process holds open, with its heap size and the processes
  holding it. Use it when windows suddenly cannot be created ("Not enough memory resources are available") or
  after a hidden test run, to see which desktops are still alive and why.

.DESCRIPTION
  A desktop (CreateDesktop: the hidden desktop of scripts/test-hidden.ps1, a Chromium sandbox's
  sbox_alternate_desktop_*, the visible Default) lives as long as any process holds a handle to it. Every process
  that connects to the window manager holds one to the desktop it was started on, even a console program that never
  opens a window (MSBuild nodes and the C# compiler server do). EnumDesktops cannot tell you who keeps a desktop
  alive, so this script reads it off the handles:

  1. The system handle list (NtQuerySystemInformation, SystemExtendedHandleInformation) gives every handle in
     every process. The ones of the desktop object type are kept; the type's index is found from this script's
     own desktop handle.
  2. Each one is duplicated into this process (only processes of your own user can be opened, which is all that
     can hold your session's desktops besides a few system ones) and grouped by object with CompareObjectHandles,
     so two handles to one desktop count as one desktop even if it has lost its name.
  3. Name and heap size come from GetUserObjectInformation (UOI_NAME, UOI_HEAPSIZE).

  It also lists processes that were STARTED on a desktop other than Default (from the DesktopInfo field of each
  process's parameters block). Those are the ones to look at when a desktop outlives the run that made it.

  Read-only: it opens processes for PROCESS_DUP_HANDLE and PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ,
  duplicates handles into itself and closes them again. It changes nothing.

  Measured with it on 2026-11-25 (docs/superpowers/notes/2026-11-25-hidden-desktop-holders.md): a test run that
  builds leaves VBCSCompiler holding the hidden desktop, and every other build on the machine then reuses that
  server, so the desktop never goes away.

.EXAMPLE
  pwsh scripts/desktop-holders.ps1
  pwsh scripts/desktop-holders.ps1 -IncludeDefault
#>
param(
    # Also list the holders of the visible Default desktop (hundreds of processes; off by default).
    [switch]$IncludeDefault
)

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopHolders
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernelbase.dll")] static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetUserObjectInformationW(IntPtr obj, int index, out int value, int length, out int needed);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

    const int SystemExtendedHandleInformation = 0x40, UOI_NAME = 2, UOI_HEAPSIZE = 5;
    const uint PROCESS_DUP_HANDLE = 0x0040, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_VM_READ = 0x0010;
    const uint DUPLICATE_SAME_ACCESS = 2;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX on x64: Object, UniqueProcessId, HandleValue (8 bytes each), GrantedAccess (4),
    // CreatorBackTraceIndex (2), ObjectTypeIndex (2), HandleAttributes (4), Reserved (4). The list starts after two
    // pointer-sized fields, NumberOfHandles and Reserved.
    const int EntrySize = 40, HeaderSize = 16, PidOffset = 8, HandleOffset = 16, TypeOffset = 30;

    public sealed class Desktop
    {
        public string Name = "";
        public int HeapKB;
        public List<string> Holders = new List<string>();
    }

    // "(no name)" is a desktop that is alive but no longer named; an unreadable name (the holder's handle lacks the
    // access to read it) is reported with its error, so the two are never confused.
    static string NameOf(IntPtr desktop)
    {
        var buffer = new StringBuilder(512);
        int needed;
        if (!GetUserObjectInformationW(desktop, UOI_NAME, buffer, buffer.Capacity * 2, out needed))
            return "(name unreadable, error " + Marshal.GetLastWin32Error() + ")";
        return buffer.Length == 0 ? "(no name)" : buffer.ToString();
    }

    static int HeapOf(IntPtr desktop)
    {
        int value, needed;
        return GetUserObjectInformationW(desktop, UOI_HEAPSIZE, out value, 4, out needed) ? value : -1;
    }

    static string ProcessLabel(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName + " " + pid; } catch { return "? " + pid; }
    }

    // Every desktop held open by a process this user can open, grouped by object.
    public static List<Desktop> Holders()
    {
        var length = 1 << 24;
        var buffer = Marshal.AllocHGlobal(length);
        int returned;
        while (NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out returned) == STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(buffer);
            length *= 2;
            buffer = Marshal.AllocHGlobal(length);
        }

        var desktops = new List<Desktop>();
        var representatives = new List<IntPtr>();
        var processes = new Dictionary<int, IntPtr>();
        try
        {
            var count = Marshal.ReadInt64(buffer);
            var me = Process.GetCurrentProcess().Id;
            var mine = GetThreadDesktop(GetCurrentThreadId());
            Func<long, IntPtr> entry = i => buffer + HeaderSize + (int)(i * EntrySize);

            // The desktop object type's index is whatever type this process's own desktop handle has.
            var desktopType = -1;
            for (long i = 0; i < count && desktopType < 0; i++)
                if ((long)Marshal.ReadIntPtr(entry(i), PidOffset) == me && Marshal.ReadIntPtr(entry(i), HandleOffset) == mine)
                    desktopType = (ushort)Marshal.ReadInt16(entry(i), TypeOffset);

            for (long i = 0; i < count; i++)
            {
                var e = entry(i);
                if ((ushort)Marshal.ReadInt16(e, TypeOffset) != desktopType) continue;
                var pid = (int)(long)Marshal.ReadIntPtr(e, PidOffset);
                IntPtr process;
                if (!processes.TryGetValue(pid, out process)) processes[pid] = process = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                if (process == IntPtr.Zero) continue;
                IntPtr copy;
                if (!DuplicateHandle(process, Marshal.ReadIntPtr(e, HandleOffset), GetCurrentProcess(), out copy, 0, false, DUPLICATE_SAME_ACCESS)) continue;

                var k = representatives.FindIndex(r => CompareObjectHandles(r, copy));
                if (k < 0)
                {
                    representatives.Add(copy);
                    desktops.Add(new Desktop { Name = NameOf(copy), HeapKB = HeapOf(copy) });
                    k = desktops.Count - 1;
                }
                else CloseHandle(copy);
                var label = ProcessLabel(pid);
                if (!desktops[k].Holders.Contains(label)) desktops[k].Holders.Add(label);
            }
        }
        finally
        {
            representatives.ForEach(r => CloseHandle(r));
            foreach (var p in processes.Values) if (p != IntPtr.Zero) CloseHandle(p);
            Marshal.FreeHGlobal(buffer);
        }
        return desktops;
    }

    static byte[] Read(IntPtr process, IntPtr address, int size)
    {
        var bytes = new byte[size];
        IntPtr read;
        return ReadProcessMemory(process, address, bytes, (IntPtr)size, out read) ? bytes : null;
    }

    // The desktop a process was started on, as its STARTUPINFO named it: PEB (+0x20) -> RTL_USER_PROCESS_PARAMETERS,
    // whose DesktopInfo UNICODE_STRING sits at +0xC0 on x64. Empty means "inherited the parent's", null means the
    // process could not be read.
    public static string StartupDesktop(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            int returned;
            if (NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf(info), out returned) != 0) return null;
            var parametersPointer = Read(process, info.PebBaseAddress + 0x20, 8);
            if (parametersPointer == null) return null;
            var desktopInfo = Read(process, (IntPtr)BitConverter.ToInt64(parametersPointer, 0) + 0xC0, 16);
            if (desktopInfo == null) return null;
            var length = BitConverter.ToUInt16(desktopInfo, 0);
            if (length == 0) return "";
            var text = Read(process, (IntPtr)BitConverter.ToInt64(desktopInfo, 8), length);
            return text == null ? null : Encoding.Unicode.GetString(text);
        }
        finally { CloseHandle(process); }
    }
}
'@

Write-Host 'Desktops held open, by object (a desktop with "(no name)" has lost its name but is still alive):'
[DesktopHolders]::Holders() |
    Where-Object { $IncludeDefault -or $_.Name -ne 'Default' } |
    ForEach-Object { '  {0}  heap {1} KB  held by {2}: {3}' -f $_.Name, $_.HeapKB, $_.Holders.Count, ($_.Holders -join ', ') }

# Started elsewhere than the visible desktop. Chromium's sandboxed children are left out: they live on their
# browser's sbox_alternate_desktop_* by design and go with the browser.
Write-Host ''
Write-Host 'Processes started on another desktop than Default:'
$default = @('', 'Default', 'WinSta0\Default')
Get-CimInstance Win32_Process | ForEach-Object {
    $desktop = [DesktopHolders]::StartupDesktop([int]$_.ProcessId)
    if ($null -ne $desktop -and $desktop -notin $default -and $desktop -notlike '*sbox_alternate_desktop_*') {
        $command = "$($_.CommandLine)" -replace '\s+', ' '
        '  {0}  {1} {2}  started {3:HH:mm:ss}  {4}' -f $desktop, $_.Name, $_.ProcessId, $_.CreationDate,
            $command.Substring(0, [Math]::Min(120, $command.Length))
    }
}
