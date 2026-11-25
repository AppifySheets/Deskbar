using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Deskbar.Core.Control;

namespace Deskbar.Windows.Tests;

// The spike (docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md) found that
// VirtualDesktop.Configure() constructs a WPF HwndSource internally and throws unless
// the calling thread is STA. xunit's default test-runner threads are MTA (the same
// "async Main is silently ignored" family of gotcha the spike documented for console
// apps -- xunit has no built-in STA test-thread support on .NET, only on old desktop-CLR
// runners). The task's dispatch instructions (not the brief, which says nothing about
// threading) offered two ways to cope: marshal the COM calls onto a dedicated STA thread
// inside the test, or use an assembly-level fixture. This class implements the former --
// a tiny, self-contained helper that runs a test body on a freshly spun-up STA thread and
// re-throws any exception (including Assert failures, which xunit implements as thrown
// exceptions) back on the calling test thread so xunit reports them normally.
//
// It is also the one door every test that creates a real window goes through, so it is where
// the live-Deskbar guard sits (see RefuseBesideALiveDeskbar).
static class StaThread
{
    public static void Run(Action body)
    {
        RefuseBesideALiveDeskbar();

        // Every twentieth test, see that every processor still has window class names to give (ClassNameSlots, task
        // c5e9b17f). A test takes its names on the processor its thread runs on and they come back on another, so on
        // a session whose table has filled once a run drains processors as it goes: measured, six tests of one class
        // refused in one burst, in a run started right after a repair. Nothing is moved on a healthy machine, where
        // the look costs under twenty milliseconds.
        if (Interlocked.Increment(ref runs) % 20 == 1) Deskbar.Windows.Recovery.ClassNameSlots.Balance();

        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            // A name nothing else has, because WPF writes the thread's name into the name of every window class it
            // registers on it, and that is how the classes are found again (GiveWindowClassesBack).
            Name = $"sta{Environment.ProcessId}.{Interlocked.Increment(ref threads)}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        GiveWindowClassesBack(thread.Name);

        failure?.Throw();
    }

    static int threads;
    static int runs;

    // GIVE THE TEST'S WINDOW CLASSES BACK ONCE ITS THREAD HAS ENDED. Every WPF Dispatcher and every HwndWrapper (one
    // behind each Window, Popup and menu) registers a window class with a unique name, and each name holds one of the
    // 16,320 slots of the session's USER atom table, which every program on every desktop of the session shares
    // (docs/superpowers/notes/2026-11-25-user-atom-table-full.md). With the threads simply ending, every test kept its
    // classes until testhost exited: 4,014 names by the end of one run, measured (task c5e9b17f). On 2026-11-25 a run
    // failed 510 of 770 with "Not enough memory resources" from Dispatcher..ctor, which read like the hidden desktop's
    // heap and was not (Deskbar task 4888c75e).
    //
    // It was not a leak in the session either, as this comment first said. Four such runs at once fill the table,
    // and a table that has filled even once refuses names on some processors for the rest of the session
    // (src/Deskbar.Windows/Recovery/ClassNameSlots.cs has what was measured). So this is not tidiness: the hoarding
    // was what broke the session.
    //
    // HOW: from outside, after the thread is gone. WPF writes the thread's name into each class name
    // (HwndWrapper[app;thread;{guid}]), Run gives every thread a name of its own, and here every name in the table is
    // read (one pass is 2.5 ms, measured) and the ones carrying this thread's name are unregistered. Windows has
    // destroyed a dead thread's windows by then, which a class needs before it can go. A run of all 1,620 tests holds
    // 9 names at its peak and takes what it took before, 1 min 39 s.
    //
    // The first version did it on the thread, before it ended: dispose the open windows, let the Dispatcher's queue
    // run down to ApplicationIdle (WPF only POSTS a class's unregistration, and a shutdown drops the queue), then
    // shut the Dispatcher down. It gave the classes back, but on the hidden desktop test-hidden.ps1 uses the queue
    // often never reaches idle, so tests sat out the five second bound: 176 tests took 5 min 29 s (task c5e9b17f).
    // It also ran whatever else a test had left queued, after the test. Nothing of the test's runs here.
    static void GiveWindowClassesBack(string threadName)
    {
        var mine = $";{threadName};";
        var name = new char[256];
        var instance = GetModuleHandleW(0);
        Enumerable.Range(0xC000, 0x4000)
            .Where(atom => new string(name, 0, GetClipboardFormatNameW((uint)atom, name, name.Length)) is var text
                && text.StartsWith("HwndWrapper[", StringComparison.Ordinal) && text.Contains(mine, StringComparison.Ordinal))
            .ToList()
            .ForEach(atom => UnregisterClassW(atom, instance));
    }

    // Answers with the name of any entry in the session's table of class names, class names included, and 0 for a
    // slot that is free.
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetClipboardFormatNameW(uint format, [System.Runtime.InteropServices.Out] char[] name, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnregisterClassW(nint classAtom, nint instance);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern nint GetModuleHandleW(nint name);

    // NO REAL WINDOWS BESIDE A RUNNING DESKBAR IN THE SAME SESSION. These tests create real top-level windows,
    // and a Deskbar running on the same desktop treats them as anyone's windows: in a tiled workspace it tiles
    // each one as it appears, re-lays out the person's own windows around it and puts them back when it
    // closes. Measured on 2026-11-22 in Deskbar's trace during a suite run ("tile: ... settled: acting=Deskbar"
    // on a testhost window, then "the last window to join left, the layout from before it is back"), dozens of
    // times a run. That did two kinds of damage:
    //   * to the person: their tiled windows jumped about for the length of the run (Petre: "when running tests
    //     on my pc, it fucks up my workflow");
    //   * to the tests: TilePreviewOverlayTests read the screen where its black window had been moved away from
    //     (the pixel was VS Code's background), and TileSurfaceTests' burst test heard the Tiler's move as a
    //     second settle. Both looked flaky and never failed on CI, where no Deskbar runs.
    // So every such test fails at once, before it opens anything, and says where to run instead. Checked per
    // test rather than once per run, so exiting Deskbar mid-run is enough.
    //
    // Only a Deskbar that can SEE these windows counts: one in this session, while the test runs on the
    // session's ordinary desktop ("Default", where Deskbar itself runs). scripts/test-hidden.ps1 starts the run
    // on a separate Win32 desktop, where no other program can enumerate the windows or hear their events, so
    // there the tests run beside a live Deskbar without touching it. A Deskbar in another signed-in account is
    // in another session and never counts.
    static void RefuseBesideALiveDeskbar()
    {
        if (!OnTheDefaultDesktop()) return;
        var session = Process.GetCurrentProcess().SessionId;
        var live = Process.GetProcesses()
            .Where(p => SafeSessionId(p) == session && OtherInstance.IsDeskbarProcessName(p.ProcessName))
            .Select(p => $"{p.ProcessName} (pid {p.Id})")
            .ToList();
        if (live.Count > 0)
            throw new InvalidOperationException(
                $"Deskbar is running in this session ({string.Join(", ", live)}). These tests open real windows, and a live " +
                "Deskbar tiles and moves them, which rearranges the person's own windows and makes the tests fail at random. " +
                "Run them on a hidden desktop instead: pwsh scripts/test-hidden.ps1 (the screen-reading tests run on CI). " +
                "Deskbar.Core.Tests opens no windows and is safe to run anywhere.");
    }

    // The name of the Win32 desktop this thread's windows go to. "Default" is the one the person sees; anything
    // else (scripts/test-hidden.ps1 uses "DeskbarTests") is invisible to the Deskbar running on it. A name that
    // cannot be read counts as Default, so the guard errs on the side of not opening windows.
    static bool OnTheDefaultDesktop()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        var name = new char[256];
        return desktop == 0
            || !GetUserObjectInformationW(desktop, UOI_NAME, name, name.Length * sizeof(char), out _)
            || string.Equals(new string(name).TrimEnd('\0'), "Default", StringComparison.OrdinalIgnoreCase);
    }

    const int UOI_NAME = 2;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetThreadDesktop(uint threadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(nint handle, int index, [System.Runtime.InteropServices.Out] char[] info, int length, out int needed);

    // A process that exits between the listing and the question has no session to report.
    static int SafeSessionId(Process process)
    {
        try { return process.SessionId; }
        catch (InvalidOperationException) { return -1; }
    }
}
