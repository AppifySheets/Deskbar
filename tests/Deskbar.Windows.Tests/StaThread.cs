using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
            finally
            {
                try
                {
                    GiveWindowClassesBack();
                }
                catch (Exception ex)
                {
                    // A window's own handler throwing while it is torn down is still a test failure, but never one
                    // that hides the test's own.
                    failure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }

    // GIVE THE TEST'S WINDOW CLASSES BACK BEFORE ITS THREAD ENDS. Every WPF Dispatcher and every HwndWrapper (one
    // behind each Window, Popup and menu) registers a window class with a unique name, and each name holds one of the
    // 16,384 slots of the session's USER atom table, which every program on every desktop of the session shares
    // (docs/superpowers/notes/2026-11-25-user-atom-table-full.md). With the threads simply ending, every test kept its
    // classes until testhost exited, so the 1,500-odd window tests needed well over 1,500 free slots by the end of a
    // run. On 2026-11-25, with about 1,700 left in a session that leaks them, a run failed 510 of 770 with "Not enough
    // memory resources" from Dispatcher..ctor, which read like the hidden desktop's heap and was not (Deskbar task
    // 4888c75e).
    //
    // Three steps, in this order, each measured by StaThreadTests:
    //   1. Dispose every presentation source still open on this thread: the windows a test never closed. Shutdown
    //      alone destroys them too, but too late, see 2.
    //   2. Let the queue run. WPF does not unregister a class when its window is destroyed; it posts the
    //      UnregisterClass to the Dispatcher. Posted during shutdown, that operation is dropped and the class stays
    //      registered (measured: the left-open window's class survived a bare InvokeShutdown).
    //   3. Shut the Dispatcher down, which disposes its own message-only window and unregisters that class at once.
    // A test that never touched WPF has no Dispatcher, and there is nothing to do.
    static void GiveWindowClassesBack()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        // A copy first: disposing a source takes it out of the list being walked.
        PresentationSource.CurrentSources
            .Cast<PresentationSource>()
            .Where(source => source.Dispatcher == dispatcher && !source.IsDisposed)
            .OfType<HwndSource>()
            .ToList()
            .ForEach(source => source.Dispose());

        // Invoke from the Dispatcher's own thread at a low priority runs everything queued above it first, the
        // posted UnregisterClass calls among them, and returns once the queue has come down to this one. Bounded, so
        // a queue that never goes idle (something re-posting itself above this priority) costs a slot, not a hang.
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle, CancellationToken.None, TimeSpan.FromSeconds(5));
        dispatcher.InvokeShutdown();
    }

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
