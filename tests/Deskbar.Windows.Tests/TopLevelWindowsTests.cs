using System.Runtime.InteropServices;
using Deskbar.Core.Domain;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// Petre: "i can't see Buzz in HRIS workspace."
//
// Buzz was running, its window visible on that desktop, and the bar had never heard of it. Measured
// with the app's own predicate:
//
//   0x1F12EA buzz-desktop vis=1 cloaked=0 owner=0x0 ex=0x00040110 class='Tauri Window' title=''
//            candidate=False
//
// The window carries WS_EX_APPWINDOW -- the explicit "put me on the taskbar" opt-in -- and has no
// title at all. IsTaskbarCandidate required text, on the reasoning that "taskbar buttons always have
// text", and that turns out to be false for a Tauri app that never sets one. Windows gives it a
// button anyway.
//
// The relaxation is narrow because it was measured before it was written. On his machine, every
// window that the old rule rejected ONLY for having no text:
//
//   with WS_EX_APPWINDOW:    1  (Buzz)
//   without it:              0
//
// So text OR an explicit opt-in, and nothing else changes.
//
// These tests make their own windows with CreateWindowEx rather than hunting for a real app's, which
// is the only way to hold the styles still: one titleless window that opts in, one that does not, and
// one with a title. Created off screen and destroyed at the end, on an STA thread like every other
// window test here.
public class TopLevelWindowsTests
{
    [Fact]
    public void A_titleless_window_that_asks_for_a_taskbar_button_is_a_candidate() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "", exStyle: WS_EX_APPWINDOW);

        Assert.True(TopLevelWindows.IsTaskbarCandidate(window.Handle));
        Assert.Contains(window.Handle, TopLevelWindows.Enumerate());
    });

    // The other half of the rule, and the reason it is not simply "drop the text test": a titleless
    // window that never asked for a button is the shape of every helper window an app leaves lying
    // around, and the bar has no row to spend on those.
    [Fact]
    public void A_titleless_window_without_the_opt_in_is_not() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "", exStyle: 0);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(window.Handle));
    });

    [Fact]
    public void A_titled_window_is_a_candidate_as_it_always_was() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "Ordinary window", exStyle: 0);

        Assert.True(TopLevelWindows.IsTaskbarCandidate(window.Handle));
    });

    // A tool window is still refused, opt-in or not... except with the opt-in, which is the existing
    // rule and is left exactly as it was: WS_EX_TOOLWINDOW keeps a window off the taskbar unless it
    // asks back in.
    [Fact]
    public void A_tool_window_is_still_refused_unless_it_opts_back_in() => StaThread.Run(() =>
    {
        using var tool = TestWindow.Create(title: "Palette", exStyle: WS_EX_TOOLWINDOW);
        using var optedIn = TestWindow.Create(title: "Palette", exStyle: WS_EX_TOOLWINDOW | WS_EX_APPWINDOW);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(tool.Handle));
        Assert.True(TopLevelWindows.IsTaskbarCandidate(optedIn.Handle));
    });

    // The Windows half of "a closed Store app's hidden frame stays out of the bar" is one DWM question,
    // IsShellCloaked. A test window cannot be cloaked by the shell without being moved to another virtual
    // desktop, which would change the desktops of whoever runs the suite, so only the answer that must
    // never be a false positive is pinned here: an ordinary window, and a handle that names nothing.
    // The decision that uses the answer is tested in Deskbar.Core.Tests (ShellHiddenFrameTests).
    [Fact]
    public void An_ordinary_window_is_not_cloaked_by_the_shell() => StaThread.Run(() =>
    {
        using var window = TestWindow.Create(title: "Ordinary window", exStyle: 0);

        Assert.False(TopLevelWindows.IsShellCloaked(window.Handle));
        Assert.False(new WindowMonitor().IsCloakedByShell(new WindowHandle(window.Handle)));
    });

    [Fact]
    public void A_handle_that_names_no_window_is_not_cloaked_by_the_shell() =>
        Assert.False(TopLevelWindows.IsShellCloaked(0x7FFFFFF0));

    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_APPWINDOW = 0x00040000;

    // A real top-level window of our own, with the styles a test asks for. "static" as a class name is
    // a stock control class that always exists, so nothing has to be registered.
    sealed class TestWindow : IDisposable
    {
        public required nint Handle { get; init; }

        public static TestWindow Create(string title, uint exStyle)
        {
            // WS_POPUP with no parent: top-level, so GetAncestor(GA_ROOT) answers itself, which the
            // predicate checks first. Parked far off the virtual screen so the suite never flashes
            // anything at whoever is running it.
            var handle = CreateWindowExW(exStyle, "static", title, WS_POPUP | WS_VISIBLE,
                -32000, -32000, 40, 40, 0, 0, 0, 0);
            Assert.NotEqual(0, handle);
            return new TestWindow { Handle = handle };
        }

        // A window of a named CLASS, which "static" cannot give us and which one exclusion needs.
        // The class has to be real: the predicate reads the class name off the live hwnd, so a
        // pretend one would test nothing.
        public static TestWindow CreateOfClass(string className, string title, uint exStyle)
        {
            EnsureClass(className);
            var handle = CreateWindowExW(exStyle, className, title, WS_POPUP | WS_VISIBLE,
                -32000, -32000, 40, 40, 0, 0, 0, 0);
            Assert.NotEqual(0, handle);
            return new TestWindow { Handle = handle };
        }

        // A window of the "static" class OWNED by another, which is how an app's dialog or settings window
        // relates to its main window. CreateWindowEx takes the owner as the parent of a WS_POPUP.
        public static TestWindow CreateOwned(nint owner, string title, uint exStyle)
        {
            var handle = CreateWindowExW(exStyle, "static", title, WS_POPUP | WS_VISIBLE,
                -32000, -32000, 40, 40, owner, 0, 0, 0);
            Assert.NotEqual(0, handle);
            return new TestWindow { Handle = handle };
        }

        // Kept in a static field on purpose: the delegate IS the window procedure, and letting it be
        // collected while a window of the class lives would crash the runner rather than fail a test.
        static readonly Dictionary<string, WndProc> Registered = [];

        static void EnsureClass(string className)
        {
            if (Registered.ContainsKey(className)) return;

            WndProc proc = DefWindowProcW;
            Registered[className] = proc;
            var registration = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                lpszClassName = className,
            };
            Assert.NotEqual(0, RegisterClassExW(ref registration));
        }

        delegate nint WndProc(nint hwnd, uint message, nint w, nint l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public int cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassExW(ref WNDCLASSEXW registration);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern nint DefWindowProcW(nint hwnd, uint message, nint w, nint l);

        public void Dispose() => DestroyWindow(Handle);

        const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(nint hwnd);
    }

    // Petre: "what's in the unplaced workspace?" One window, and it was DWM's ghost -- the greyed
    // stand-in Windows paints over an app that has stopped responding:
    //
    //   0x703DC  pid=15908 (dwm)  class='Ghost'  title=''  ex=0x00040110 (WS_EX_APPWINDOW)
    //
    // Titleless AND carrying the taskbar opt-in, which is precisely the pair this rule was relaxed to
    // admit for Buzz. The measurement behind that relaxation could not have caught it: a ghost exists
    // only while something is hung, so there was none on the machine when every window was counted.
    [Fact]
    public void The_shells_ghost_window_is_not_a_candidate()
    {
        using var ghost = TestWindow.CreateOfClass("Ghost", "", WS_EX_APPWINDOW);
        using var ordinary = TestWindow.CreateOfClass("NotAGhost", "", WS_EX_APPWINDOW);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(ghost.Handle));

        // The control: same styles, same absent title, a class nobody excludes. Without this the test
        // would pass just as well against a rule that had stopped admitting titleless windows at all,
        // which is the fix it is guarding.
        Assert.True(TopLevelWindows.IsTaskbarCandidate(ordinary.Handle));
    }

    // Clicking the tray's overflow arrow repeatedly flashed a window of class Xaml_WindowedPopupClass,
    // titled "PopupHost", for about 0.2 s each time: visible, not cloaked, not a tool window, so it was a
    // candidate and the bar showed it as an Unplaced row. The class is the XAML popup surface, never an
    // app's own window.
    [Fact]
    public void A_xaml_popup_surface_is_not_a_candidate()
    {
        using var popup = TestWindow.CreateOfClass("Xaml_WindowedPopupClass", "PopupHost", 0);
        using var ordinary = TestWindow.CreateOfClass("NotAPopupSurface", "PopupHost", 0);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(popup.Handle));
        Assert.DoesNotContain(popup.Handle, TopLevelWindows.Enumerate());

        // The control: same title and styles, another class, so the test cannot pass against a rule that
        // had started refusing everything titled "PopupHost".
        Assert.True(TopLevelWindows.IsTaskbarCandidate(ordinary.Handle));
    }

    // The owner is not the rule: a window owned by a window that is not on the taskbar, as a tray app's
    // window can be owned by its hidden main window, stays a candidate.
    [Fact]
    public void A_window_owned_by_a_non_candidate_is_still_a_candidate()
    {
        using var hiddenOwner = TestWindow.CreateOfClass("OwnerNobodyListed", "", 0);
        using var owned = TestWindow.CreateOwned(hiddenOwner.Handle, "Settings", 0);

        Assert.False(TopLevelWindows.IsTaskbarCandidate(hiddenOwner.Handle));
        Assert.True(TopLevelWindows.IsTaskbarCandidate(owned.Handle));
    }
}
