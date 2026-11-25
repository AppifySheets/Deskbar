<#
.SYNOPSIS
  Tells whether every processor of this machine can still register window classes, and with -Repair moves class
  name slots onto the processors that cannot. When one cannot, RegisterClassEx fails there with error 8, "Not
  enough memory resources are available to process this command", and every new WPF window with it.

.DESCRIPTION
  Every window class name, registered window message and clipboard format name of a Windows session comes out of
  one table (the "USER atom table") with 16,320 slots, shared by every program on every desktop of the session.

  What goes wrong with it is NOT that it is full, and not a leak (Deskbar tasks 4d419051 and c5e9b17f, 2026-11-25;
  docs/superpowers/notes/2026-11-25-user-atom-table-full.md has the measurements and the kernel code they were
  checked against). Windows keeps the free slots in one list per processor. If the table has been full even once
  in the session, entries that can never be a name are left in those lists, and a processor with one of them on
  top of its list refuses every class name, while other processors have thousands of free slots. A thread succeeds
  or fails by the processor it happens to run on, which is why it looks random and why a probe that tries from one
  thread says "full" one second and "room" the next.

  So this measures per processor: a thread is pinned to each one in turn and registers up to -Floor uniquely named
  classes, then gives them back in the same order. -Repair tops up every processor that is short, to -Target, with
  slots taken on processors that have plenty: a slot goes back to the list of the processor that releases it. That
  repairs a session without signing out. It does not remove the unusable entries (nothing in user mode can), so
  slots drift again as programs come and go; a running Deskbar repeats the repair every 30 seconds
  (src/Deskbar.Windows/Recovery/ClassNameSlots.cs, which this script mirrors).

  The table is left alone when it is nearly full for real (-Top shows who holds the names): the probe's own
  registrations could then be the ones that fill it.

  Exit code 0 when every processor can register -Floor classes (after the repair, with -Repair), 1 otherwise.

.EXAMPLE
  pwsh scripts/user-atoms.ps1
  pwsh scripts/user-atoms.ps1 -Repair
  pwsh scripts/user-atoms.ps1 -Top 20
#>
param(
    # Top up the processors that are short.
    [switch]$Repair,

    # A processor that cannot register this many classes is short. A WPF app's start registers about thirty.
    [int]$Floor = 64,

    # What a short processor is topped up to, and what a processor that gives slots keeps.
    [int]$Target = 128,

    # How many of the most common name shapes to list (GUIDs and long hex runs folded, so one program holding
    # thousands of names shows as one line with a big count). 0 lists none.
    [int]$Top = 0
)

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class ClassNameSlots
{
    // 64 pages of 255 handle entries have an index a name can use.
    public const int Capacity = 64 * 255;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct GROUP_AFFINITY { public UIntPtr Mask; public ushort Group, R0, R1, R2; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESSOR_NUMBER { public ushort Group; public byte Number, Reserved; }

    delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterClassW(IntPtr atom, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatNameW(uint format, StringBuilder name, int max);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")] static extern bool SetThreadGroupAffinity(IntPtr thread, ref GROUP_AFFINITY affinity, IntPtr previous);
    [DllImport("kernel32.dll")] static extern void GetCurrentProcessorNumberEx(out PROCESSOR_NUMBER at);

    // Kept in a static so the delegate outlives every class registered with it.
    static readonly WndProc Proc = DefWindowProc;
    static readonly IntPtr Instance = GetModuleHandleW(IntPtr.Zero);
    static readonly string Tag = Guid.NewGuid().ToString("N");
    static int serial;

    // Every logical processor as group and number; a thread is pinned to one processor of one group.
    static readonly List<Tuple<ushort, byte>> Processors = Enumerable.Range(0, GetActiveProcessorGroupCount())
        .SelectMany(g => Enumerable.Range(0, (int)GetActiveProcessorCount((ushort)g)).Select(n => Tuple.Create((ushort)g, (byte)n))).ToList();

    // Every name in the table answers to GetClipboardFormatName, class names included.
    public static List<string> Named()
    {
        var names = new List<string>(); var buffer = new StringBuilder(1024);
        for (uint atom = 0xC000; atom <= 0xFFFF; atom++) { buffer.Clear(); if (GetClipboardFormatNameW(atom, buffer, buffer.Capacity) > 0) names.Add(buffer.ToString()); }
        return names;
    }

    static bool Pin(int processor)
    {
        var p = Processors[processor];
        var affinity = new GROUP_AFFINITY { Mask = (UIntPtr)(1UL << p.Item2), Group = p.Item1 };
        if (!SetThreadGroupAffinity(GetCurrentThread(), ref affinity, IntPtr.Zero)) return false;
        for (var i = 0; i < 1000; i++) { PROCESSOR_NUMBER at; GetCurrentProcessorNumberEx(out at); if (at.Group == p.Item1 && at.Number == p.Item2) return true; Thread.Yield(); }
        return false;
    }

    // Up to `count` classes on the processor the thread is pinned to, stopping at the first refusal.
    static List<ushort> Take(int count)
    {
        var taken = new List<ushort>();
        for (var i = 0; i < count; i++)
        {
            var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = Instance,
                lpszClassName = "ClassNameSlots[" + Tag + ";" + (serial++) + "]" };
            var atom = RegisterClassExW(ref wc);
            if (atom == 0) break;
            taken.Add(atom);
        }
        return taken;
    }

    static void Release(IEnumerable<ushort> slots) { foreach (var slot in slots) UnregisterClassW((IntPtr)slot, Instance); }

    // In the opposite order to the one they were taken in, which leaves the list as it was: last in, first out.
    static void PutBack(List<ushort> slots) { Release(Enumerable.Reverse(slots)); }

    // How many classes each processor can register, counted up to `upTo`; -1 where the thread cannot be pinned.
    public static int[] Survey(int upTo) { return OnOwnThread(() => SurveyHere(upTo)); }

    static int[] SurveyHere(int upTo)
    {
        return Enumerable.Range(0, Processors.Count).Select(processor =>
        {
            if (!Pin(processor)) return -1;
            var taken = Take(upTo); PutBack(taken);
            return taken.Count;
        }).ToArray();
    }

    // Tops up every processor below `floor` to `target`, from processors that keep at least `target` themselves.
    // Returns how many slots moved.
    public static int Repair(int floor, int target)
    {
        return OnOwnThread(() =>
        {
            var have = SurveyHere(floor);
            var donors = Enumerable.Range(0, have.Length).Where(p => have[p] >= floor).ToList();
            var moved = 0;
            foreach (var poor in Enumerable.Range(0, have.Length).Where(p => have[p] >= 0 && have[p] < floor))
            {
                var need = target - have[poor];
                while (donors.Count > 0)
                {
                    if (!Pin(donors[0])) { donors.RemoveAt(0); continue; }
                    var taken = Take(need + target);
                    if (taken.Count < need + target) { PutBack(taken); donors.RemoveAt(0); continue; }
                    PutBack(taken.GetRange(need, target));
                    Pin(poor); Release(taken.GetRange(0, need));
                    moved += need;
                    break;
                }
            }
            return moved;
        });
    }

    // Pinning is per thread, so all of it runs on a thread that ends with the call.
    static T OnOwnThread<T>(Func<T> work)
    {
        var result = default(T);
        var thread = new Thread(() => result = work());
        thread.Start(); thread.Join();
        return result;
    }
}
'@

$named = [ClassNameSlots]::Named()
"Names in use: $($named.Count) of $([ClassNameSlots]::Capacity)."

# Nearly full for real: something is holding thousands of names, and probing could be what fills the table.
if ($named.Count -ge [ClassNameSlots]::Capacity - 2048) {
    "FULL: the table of class names is nearly full for real. Find what holds the names (-Top 20) and end it; do not probe further, a table that fills once stays broken until sign-out."
    $Top = [Math]::Max($Top, 15)
    $code = 1
}
else {
    $have = [ClassNameSlots]::Survey($Floor)
    "Classes each processor can register, up to ${Floor}: " + ((0..($have.Count - 1) | ForEach-Object { "$_=$($have[$_])" }) -join ' ')
    $short = @(0..($have.Count - 1) | Where-Object { $have[$_] -ge 0 -and $have[$_] -lt $Floor })
    if ($short.Count -eq 0) { "OK: every processor can register $Floor window classes."; $code = 0 }
    elseif (-not $Repair) {
        "SHORT: $($short.Count) of $($have.Count) processors cannot register $Floor window classes ($($short -join ',')). A program whose thread runs there gets error 8, 'Not enough memory resources'. Run again with -Repair; no sign-out is needed."
        $code = 1
    }
    else {
        $moved = [ClassNameSlots]::Repair($Floor, $Target)
        $after = [ClassNameSlots]::Survey($Floor)
        $still = @(0..($after.Count - 1) | Where-Object { $after[$_] -ge 0 -and $after[$_] -lt $Floor })
        if ($still.Count -eq 0) { "REPAIRED: $($short.Count) processors were short ($($short -join ',')); $moved slots moved onto them, every processor can register $Floor window classes now."; $code = 0 }
        else { "STILL SHORT: $moved slots moved, but processors $($still -join ',') still cannot register $Floor window classes: no processor has enough to spare. Close programs, or sign out."; $code = 1 }
    }
}

if ($Top -gt 0) {
    ""
    "Most common name shapes:"
    $named |
        ForEach-Object { ($_ -replace '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '{guid}') -replace '[0-9a-fA-F]{6,}', '#hex' } |
        Group-Object | Sort-Object Count -Descending | Select-Object -First $Top Count, Name | Format-Table -AutoSize | Out-String -Width 200
}

exit $code
