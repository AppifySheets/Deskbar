using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Windows.Shell;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// I-036: Settings and Calculator both showed the same blue window icon on the bar. Both are store apps, hosted by
// ApplicationFrameHost, which answers no icon for the window, so the exe fallback (the host's icon) was all there was.
// The shell knows each window's app (AppUserModelID), so IconCache asks it for that app's icon on a pool thread and
// takes the answer back through the bar's probe. These pin that path with a fake shell and made-up window handles,
// since the cache is static.
public class IconCacheHostedAppTests(ITestOutputHelper output)
{
    const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";

    // A fake shell: AppIdOf answers from `appIdByCall`, called with that window's own call number (from 1), and
    // ImageOfApp answers `image`. Everything else is unused by IconCache. Calls are counted per window, because
    // frame windows left pending by one test are probed during the next, through this same seam.
    sealed class FakeShellPins(Func<int, string?> appIdByCall, PinImage image) : ITaskbarPins
    {
        readonly object gate = new();
        readonly Dictionary<nint, int> appIdCallsByWindow = [];
        public int ImageCalls { get; private set; }

        public int AppIdCallsFor(WindowHandle window)
        {
            lock (gate) return appIdCallsByWindow.GetValueOrDefault(window.Value);
        }

        public string? AppIdOf(WindowHandle window)
        {
            lock (gate)
            {
                var call = appIdCallsByWindow.GetValueOrDefault(window.Value) + 1;
                appIdCallsByWindow[window.Value] = call;
                return appIdByCall(call);
            }
        }

        public bool WasAskedFor(WindowHandle window)
        {
            lock (gate) return appIdCallsByWindow.ContainsKey(window.Value);
        }

        // Run on the asking thread as the image is handed over, for a test that looks at that thread.
        public Action? OnImageOfApp { get; init; }

        public Maybe<PinImage> ImageOfApp(string appId, int size)
        {
            lock (gate) ImageCalls++;
            OnImageOfApp?.Invoke();
            return image;
        }

        public IReadOnlyList<TaskbarPin> Read() => [];
        public int Version => 0;
        public Maybe<PinImage> ImageOf(TaskbarPin pin, int size) => Maybe<PinImage>.None;
        public Result Launch(TaskbarPin pin, string? arguments = null) => throw new NotSupportedException();
        public Result Reveal(TaskbarPin pin) => throw new NotSupportedException();
        public Result Unpin(TaskbarPin pin) => throw new NotSupportedException();
        public string? NameOfApp(string appId) => null;
    }

    // Opaque, so IconCache.IsBlank keeps it.
    static PinImage OpaqueAppIcon()
    {
        const int size = 32;
        const int stride = size * 4;
        var pixels = Enumerable.Range(0, stride * size).Select(i => i % 4 == 3 ? (byte)0xFF : (byte)0x80).ToArray();
        return new PinImage(size, size, pixels);
    }

    // What a loading store app hands over: a valid picture in which every pixel is fully transparent.
    static PinImage BlankAppIcon() => new(32, 32, new byte[32 * 32 * 4]);

    static string? PrintOf(ImageSource? icon) => IconCache.Fingerprint((BitmapSource)icon!);

    // Swaps the seam in for one test and puts the real one back, even when the test fails.
    static void WithShell(ITaskbarPins shell, Action body)
    {
        var original = IconCache.Shell;
        IconCache.Shell = shell;
        try { body(); }
        finally { IconCache.Shell = original; }
    }

    // The bar's icon watch, as a bounded loop: one probe a second apart, since a window is probed at most once a second.
    static void ProbeUntil(Func<bool> done)
    {
        for (var i = 0; i < 5 && !done(); i++)
        {
            Thread.Sleep(1100);
            IconCache.ProbePending();
        }
    }

    [Fact]
    public void A_store_app_window_gets_its_apps_icon() => StaThread.Run(() =>
    {
        var image = OpaqueAppIcon();
        var shell = new FakeShellPins(_ => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", image);
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0001);
            IconCache.For(handle, FrameHost, null);
            Assert.True(IconCache.IsPending(handle, FrameHost), "a frame window with no icon of its own should still be asked");

            ProbeUntil(() => !IconCache.IsPending(handle, FrameHost));

            Assert.False(IconCache.IsPending(handle, FrameHost));
            var icon = IconCache.For(handle, FrameHost, null);
            Assert.NotNull(icon);
            Assert.Equal(PrintOf(IconCache.ToImageSource(image)), PrintOf(icon));
            Assert.Equal(1, shell.AppIdCallsFor(handle));
        });
    });

    // How long The_icon_arrives_without_waiting_for_a_probe waits for the event. It used to be 900 ms, chosen to sit
    // under the bar's one-second probe interval, and it failed a release run at 920 ms
    // (https://github.com/AppifySheets/Deskbar/actions/runs/45574480669/attempts/1) while passing everywhere else.
    // The bound was never what proved the point: nothing in this test calls ProbePending and no bar is running, so
    // the only thing that can raise the event is the ask that IconCache.For itself started. The wait only has to be
    // long enough for a pool thread to be scheduled on a busy shared runner, where one queued work item can wait
    // half a second or more for the pool to add a thread. A real failure (no ask started) still fails, just later.
    static readonly TimeSpan ArrivalWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_icon_arrives_without_waiting_for_a_probe() => StaThread.Run(() =>
    {
        // No id on the first read, the id after. The shell's own retry finds it on the pool thread, 50 ms later.
        var image = OpaqueAppIcon();
        var shell = new FakeShellPins(call => call == 1 ? null : "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", image);
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0005);
            var arrived = new ManualResetEventSlim();
            Action onArrived = () => arrived.Set();
            IconCache.HostedIconArrived += onArrived;
            try
            {
                var clock = Stopwatch.StartNew();
                IconCache.For(handle, FrameHost, null);
                var raised = arrived.Wait(ArrivalWait);
                output.WriteLine($"arrived={raised} after {clock.ElapsedMilliseconds} ms, app id reads {shell.AppIdCallsFor(handle)}, image calls {shell.ImageCalls}");
                // If this fails again, the message says how far the ask got: no reads means the pool thread never
                // ran it, reads without an image call means the id never came, an image call means the icon was
                // made but not raised.
                Assert.True(raised,
                    $"the icon should be raised without waiting for a probe (after {clock.ElapsedMilliseconds} ms: " +
                    $"{shell.AppIdCallsFor(handle)} app id read(s), {shell.ImageCalls} image call(s))");
            }
            finally { IconCache.HostedIconArrived -= onArrived; }

            // No Sleep and no ProbePending: the answer is already waiting for the next lookup.
            var icon = IconCache.For(handle, FrameHost, null);
            Assert.NotNull(icon);
            Assert.Equal(PrintOf(IconCache.ToImageSource(image)), PrintOf(icon));
            Assert.False(IconCache.IsPending(handle, FrameHost));
        });
    });

    // The pool thread hands over raw pixels and makes no WPF object. A BitmapSource made there gave the pool thread a
    // Dispatcher, and with it a hidden window; when that window could not be made the ask threw, was swallowed, and
    // the icon waited for the next probe (measured on the hidden test desktop, see IconCache.AskShell). The event is
    // raised on the same pool thread right after the ask, so the handler sees whether the ask left a Dispatcher
    // behind. Compared with the thread as the shell handed over the image, because in the full suite a pool thread
    // may already carry a Dispatcher from some other test's work; what is pinned is that THIS ask did not make one.
    [Fact]
    public void The_pool_thread_makes_no_wpf_object() => StaThread.Run(() =>
    {
        static bool HasDispatcher() => System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) is not null;
        bool? hadDispatcher = null;
        var shell = new FakeShellPins(_ => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", OpaqueAppIcon())
        {
            OnImageOfApp = () => hadDispatcher = HasDispatcher(),
        };
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0006);
            var arrived = new ManualResetEventSlim();
            bool? poolThreadHasDispatcher = null;
            Action onArrived = () =>
            {
                poolThreadHasDispatcher = HasDispatcher();
                arrived.Set();
            };
            IconCache.HostedIconArrived += onArrived;
            try
            {
                IconCache.For(handle, FrameHost, null);
                Assert.True(arrived.Wait(ArrivalWait), "the icon should arrive");
            }
            finally { IconCache.HostedIconArrived -= onArrived; }

            Assert.Equal(hadDispatcher, poolThreadHasDispatcher);
            // And the UI-thread lookup still turns the pixels into the icon.
            Assert.NotNull(IconCache.For(handle, FrameHost, null));
        });
    });

    [Fact]
    public void A_store_app_window_with_no_app_id_yet_keeps_being_asked() => StaThread.Run(() =>
    {
        // The first ask reads the id AppIdTries times and finds none. From the next read on: the id.
        var shell = new FakeShellPins(call => call <= IconCache.AppIdTries ? null : "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", OpaqueAppIcon());
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0002);
            IconCache.For(handle, FrameHost, null);

            // The first ask runs on a pool thread. Wait (bounded) for all its reads, then a moment for it to return.
            for (var i = 0; i < 100 && shell.AppIdCallsFor(handle) < IconCache.AppIdTries; i++) Thread.Sleep(20);
            Thread.Sleep(100);
            Assert.True(IconCache.IsPending(handle, FrameHost), "still pending after the failed ask, which had no id");

            // The probe a second on asks again, on a pool thread. Wait (bounded) for that ask to make its read.
            Thread.Sleep(1100);
            IconCache.ProbePending();
            for (var i = 0; i < 50 && shell.AppIdCallsFor(handle) <= IconCache.AppIdTries; i++) Thread.Sleep(20);
            Assert.True(shell.AppIdCallsFor(handle) > IconCache.AppIdTries, "the window should be asked again");

            ProbeUntil(() => !IconCache.IsPending(handle, FrameHost));

            Assert.False(IconCache.IsPending(handle, FrameHost), "settled once the id arrived");
        });
    });

    [Fact]
    public void A_blank_app_icon_is_not_final() => StaThread.Run(() =>
    {
        var shell = new FakeShellPins(_ => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", BlankAppIcon());
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0003);
            IconCache.For(handle, FrameHost, null);
            Thread.Sleep(1100);
            IconCache.ProbePending();
            Thread.Sleep(1100);
            IconCache.ProbePending();

            Assert.True(shell.WasAskedFor(handle), "the window's app should have been asked for");
            Assert.True(shell.ImageCalls >= 1, "the blank icon should have been fetched");
            Assert.True(IconCache.IsPending(handle, FrameHost), "a fully transparent icon is not an answer");
        });
    });

    [Fact]
    public void Other_windows_never_ask_the_shell() => StaThread.Run(() =>
    {
        var shell = new FakeShellPins(_ => "Some.App_123!App", OpaqueAppIcon());
        WithShell(shell, () =>
        {
            var handle = new WindowHandle((nint)0x5A0004);
            IconCache.For(handle, @"C:\Program Files\Some\app.exe", null);
            Thread.Sleep(1100);
            IconCache.ProbePending();
            Thread.Sleep(1100);
            IconCache.ProbePending();

            Assert.True(IconCache.IsPending(handle, @"C:\Program Files\Some\app.exe"), "the probes did run for this window");
            Assert.False(shell.WasAskedFor(handle));
            Assert.Equal(0, shell.AppIdCallsFor(handle));
        });
    });
}
