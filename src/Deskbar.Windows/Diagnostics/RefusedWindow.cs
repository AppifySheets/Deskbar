using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deskbar.Windows.Diagnostics;

// WINDOWS REFUSING TO MAKE A WINDOW, and whether the app can live through it (task c1e6c1ae).
//
// On 2026-11-25 between 01:40 and 01:54 the desktop heap ran out on Petre's machine. Every program trying to make a
// window got the same answer, Deskbar included, and Deskbar died of it eight times. The event log has all eight, each
// one the same exception:
//
//   System.ComponentModel.Win32Exception (8): Not enough memory resources are available to process this command.
//      at MS.Win32.HwndWrapper..ctor(...)
//
// raised from wherever WPF happened to need a new HWND: the dispatcher's own message window in `new App()`, the
// system-resources listener window while App.xaml loaded, a focus ring's frame during a bar rebuild, and the tiling
// layer's work-area listener. 8 is ERROR_NOT_ENOUGH_MEMORY. The window count was ordinary (about 1,080 windows), so the
// heap that ran out was the desktop's, shared by every process on it, and not anything this process owns.
//
// WHAT IT WAS, found the same day (task c5e9b17f, Recovery/ClassNameSlots.cs): not the desktop heap. When the same
// exception stopped nine starts between 13:11 and 13:21, the refused call was measured: RegisterClassEx, for want of a
// window CLASS NAME slot on the processor the thread ran on, while other processors had thousands. The night's eight
// carried the identical exception from the identical call, and began while several runs of the window tests were
// going, which is what fills that table, so they were very probably the same; that session ended in a reboot before
// anything was measured. "The desktop is full" below therefore reads "the session is refusing windows", and the
// decision is unchanged, because either way the shortage is not this process's own. What changed is that it can now
// be cured from here: ClassNameSlots moves slots onto the processors that are short, before the first window of every
// start and whenever a refusal is caught.
//
// That distinction is the whole decision, so it is made here and nowhere else:
//
//   * The DESKTOP is full (8 or 14). Restarting Deskbar cannot help: the new copy asks the same desktop for windows and
//     dies before it has a dispatcher (five of the eight crashes were exactly that). Living through it can: the bar,
//     the strips and the tray icon already exist, and what fails is the next ring frame, tooltip or menu. So the
//     running app carries on without that one window (Survivable), and a copy still starting waits and tries again
//     (RefusedStart).
//   * THIS PROCESS has used up its own quota of window or GDI objects (10,000 of each by default). Then the fault is a
//     leak of ours, and a fresh process is the cure, so the ordinary crash path (restore titles, start the next copy)
//     stays. ERROR_NO_MORE_USER_HANDLES (1158) says so outright; for the other two codes the counts are read
//     (GuiObjects), because Windows reports a GDI quota running out as plain "not enough memory" too.
//
// The whole exception chain is looked at, as KnownWpfFaults does: XAML loading wraps the fault in a XamlParseException
// (the 01:42, 01:43 and 01:52 crashes), and the dispatcher can wrap what it catches.
//
// Matched on the TYPE and the CODE, not on the stack. Any Win32 "not enough memory" in this app means the same thing
// whichever call met it, and the stacks seen so far run through four unrelated places; matching on HwndWrapper would
// have covered them only by luck.
public static class RefusedWindow
{
    // ERROR_NOT_ENOUGH_MEMORY, the code every one of the eight crashes carried.
    public const int NotEnoughMemory = 8;

    // ERROR_OUTOFMEMORY, its sibling; user32 and GDI answer with either.
    public const int OutOfMemory = 14;

    // ERROR_NO_MORE_USER_HANDLES: this process hit its own quota of window objects. Always a leak of ours.
    public const int NoMoreUserHandles = 1158;

    // Whether Windows refused a resource it needed for a window, for whatever reason.
    public static bool Is(Exception fault) => Codes(fault).Any(c => c is NotEnoughMemory or OutOfMemory or NoMoreUserHandles);

    // Whether the app may carry on past this fault: Windows refused for want of memory, and the counts show the shortage
    // is not this process's own (see the header).
    public static bool Survivable(Exception fault, GuiObjects mine) =>
        Codes(fault).Any(c => c is NotEnoughMemory or OutOfMemory)
        && !Codes(fault).Contains(NoMoreUserHandles)
        && !mine.NearQuota;

    // The same, with this process's counts read now. One GetGuiResources call per kind, cheap enough for a fault path.
    public static bool Survivable(Exception fault) => Survivable(fault, GuiObjects.OfThisProcess());

    // Whether a window can be made right now: one is created and destroyed at once. Message-only (parent HWND_MESSAGE),
    // so it is never on screen and in no window list (EnumWindows does not see it, nor does WindowMonitor), with the
    // system's STATIC class so nothing needs registering, while still coming out of the same desktop heap as every other
    // window here. Raw CreateWindowEx rather than WPF, because it is asked before there is any WPF in the process.
    // A yes for a small window does not promise one for the app's; whoever acts on it still survives a no (RefusedStart).
    // In particular it says nothing about class names, because it registers none: a start refused a class name passes
    // this probe at once. That case is seen to by Program.RunApp, which balances the class name slots before its first
    // window on every start, this successor's included (ClassNameSlots).
    public static bool CanCreateWindow()
    {
        var probe = CreateWindowExW(0, "STATIC", "Deskbar window probe", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
        if (probe == 0) return false;
        DestroyWindow(probe);
        return true;
    }

    static readonly nint HwndMessage = -3;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(nint hwnd);

    static IEnumerable<int> Codes(Exception fault) => Chain(fault).OfType<Win32Exception>().Select(e => e.NativeErrorCode);

    static IEnumerable<Exception> Chain(Exception? fault)
    {
        for (var at = fault; at is not null; at = at.InnerException)
            yield return at;
    }
}

// How many window (USER) and GDI objects a process holds, and whether that is close to the per-process quota.
public readonly record struct GuiObjects(int User, int Gdi)
{
    // Windows' default quota for each kind is 10,000 per process (USERProcessHandleQuota, GDIProcessHandleQuota under
    // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows). Nine tenths of it is "nearly out": a process that
    // close has a leak, whatever the rest of the desktop is doing. Deskbar's own count in normal use is in the hundreds.
    public const int NearQuotaAt = 9_000;

    public bool NearQuota => User >= NearQuotaAt || Gdi >= NearQuotaAt;

    public override string ToString() => $"{User} window objects, {Gdi} GDI objects";

    // The pseudo-handle for the current process (GetCurrentProcess), which needs no closing.
    public static GuiObjects OfThisProcess() =>
        new((int)GetGuiResources(-1, GR_USEROBJECTS), (int)GetGuiResources(-1, GR_GDIOBJECTS));

    const uint GR_GDIOBJECTS = 0;
    const uint GR_USEROBJECTS = 1;

    [DllImport("user32.dll")]
    static extern uint GetGuiResources(nint process, uint flags);
}
