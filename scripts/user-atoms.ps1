<#
.SYNOPSIS
  Tells whether this Windows session's USER atom table is full: the table every window class name, registered
  window message and clipboard format name comes from. When it is full, RegisterClassEx fails with error 8, "Not
  enough memory resources are available to process this command", and every WPF window with it, on every desktop
  of the session, whatever the desktop heap.

.DESCRIPTION
  Why this script exists (Deskbar task 4d419051, 2026-11-25): from about 12:20 every hidden-desktop test run failed
  with "Not enough memory resources" while creating its first windows, at any heap size, and the desktop heap, GDI
  and the window count all measured fine. The failing call was RegisterClassEx, inside WPF's HwndWrapper, and what it
  ran out of was atoms, not memory. See docs/superpowers/notes/2026-11-25-user-atom-table-full.md.

  The table has 16,384 string slots (0xC000-0xFFFF) for the whole session. This script measures it three ways, all
  of which are needed because the obvious one lies:

  1. Named atoms. GetClipboardFormatName answers for any atom in the table, the documented way to list it (Raymond
     Chen, "How can I investigate the possibility of a lot of leaked window classes", 2017-10-06). Healthy is well
     under 2,000. BUT a leaked class atom, one whose process is gone, no longer answers: on 2026-11-25 only about
     850 atoms had names while about 15,500 slots were taken. A low count here does NOT mean the table has room.
  2. Headroom. Registers up to -Probe uniquely named classes, then releases them all at once. Cheap and harmless at
     the default 200. Fewer than that means the table is full or nearly: anything that registers a class (any WPF
     window, Deskbar's included) can fail at that moment.
  3. Control. The same number of registrations under an INTEGER class name (MAKEINTATOM), which never touches the
     atom table. If these succeed while the string names fail, it is the atom table and nothing else.

  Read-only apart from the classes it registers for a moment and releases. Nothing frees a full table but ending
  the session: sign out, or reboot.

.EXAMPLE
  pwsh scripts/user-atoms.ps1
  pwsh scripts/user-atoms.ps1 -Probe 500 -Top 20
#>
param(
    # How many uniquely named classes to register to measure headroom. Kept small on purpose: on a nearly full
    # table every one taken is one another program cannot have until it is released, a few milliseconds later.
    [int]$Probe = 200,

    # How many of the most common name shapes to list (GUIDs and long hex runs folded, so a leak of one kind of
    # name shows as one line with a big count).
    [int]$Top = 15
)

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UserAtoms
{
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterClassW(IntPtr name, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatNameW(uint format, StringBuilder name, int max);

    // Kept in a static so the delegate outlives every class registered with it.
    static readonly WndProc Proc = DefWindowProc;

    // Every atom in 0xC000-0xFFFF that still answers with a name.
    public static List<string> Named()
    {
        var names = new List<string>();
        var buffer = new StringBuilder(1024);
        for (uint atom = 0xC000; atom <= 0xFFFF; atom++)
        {
            buffer.Clear();
            if (GetClipboardFormatNameW(atom, buffer, buffer.Capacity) > 0) names.Add(buffer.ToString());
        }
        return names;
    }

    // Registers up to `count` classes, under unique string names or (integer) under MAKEINTATOM names that skip
    // the atom table, holding them all, then releases them. Returns how many registered and the last error.
    public static int[] Headroom(int count, bool integer)
    {
        var instance = GetModuleHandleW(IntPtr.Zero);
        var tag = Guid.NewGuid().ToString("N");
        var held = new List<IntPtr>();
        var strings = new List<IntPtr>();
        var lastError = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                // Integer names 0x2000 and up: below 0xC000, so the class name is the number itself.
                var name = integer ? (IntPtr)(0x2000 + i) : Marshal.StringToHGlobalUni("UserAtomsProbe[" + tag + ";" + i + "]");
                if (!integer) strings.Add(name);
                var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = instance, lpszClassName = name };
                var atom = RegisterClassExW(ref wc);
                if (atom == 0) lastError = Marshal.GetLastWin32Error();
                else held.Add(integer ? name : (IntPtr)atom);
            }
            return new[] { held.Count, lastError };
        }
        finally
        {
            held.ForEach(name => UnregisterClassW(name, instance));
            strings.ForEach(Marshal.FreeHGlobal);
        }
    }
}
'@

$named = [UserAtoms]::Named()
$strings = [UserAtoms]::Headroom($Probe, $false)
$integers = [UserAtoms]::Headroom($Probe, $true)

"Named atoms: $($named.Count) of 16384 (a low number does not mean room: leaked class atoms have no name)"
"Headroom: $($strings[0]) of $Probe string-named classes registered$(if ($strings[1]) { " (refused with error $($strings[1]))" })"
"Control:  $($integers[0]) of $Probe integer-named classes registered$(if ($integers[1]) { " (refused with error $($integers[1]))" })"

# The verdict, in the words the next person needs.
if ($strings[0] -lt $Probe -and $integers[0] -eq $Probe) {
    "FULL: the session's USER atom table has no room. RegisterClassEx, and so every new WPF window, fails with error 8 at random. Only signing out or rebooting empties it; see docs/superpowers/notes/2026-11-25-user-atom-table-full.md."
}
elseif ($strings[0] -lt $Probe) { "Both kinds of class registration were refused: not (only) the atom table. Look at memory and the desktop heap." }
else { "OK: the atom table has room for at least $Probe more class names." }

""
"Most common named shapes:"
$named |
    ForEach-Object { ($_ -replace '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '{guid}') -replace '[0-9a-fA-F]{6,}', '#hex' } |
    Group-Object | Sort-Object Count -Descending | Select-Object -First $Top Count, Name | Format-Table -AutoSize | Out-String -Width 200
