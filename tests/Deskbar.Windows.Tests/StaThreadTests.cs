using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Deskbar.Windows.Tests;

// WPF registers a window class with a unique name ("HwndWrapper[testhost;;{guid}]") for every Dispatcher's own
// message-only window and for every HwndWrapper behind a Window. Each of those names takes a slot in the session's
// USER atom table, which has 16,320 slots shared by every program and every desktop in the session
// (docs/superpowers/notes/2026-11-25-user-atom-table-full.md). A slot comes back when the class is unregistered,
// and WPF unregisters it when the wrapper is disposed, which for the Dispatcher's own window is only at Dispatcher
// shutdown.
//
// StaThread.Run used to let each test's thread simply end, so no Dispatcher was ever shut down and every test kept
// its classes until testhost exited: 4,014 names by the end of one run, measured later (task c5e9b17f). On
// 2026-11-25 a run of Deskbar.Windows.Tests failed 510 of 770 with "Not enough memory resources" from
// Dispatcher..ctor before it crashed. That looked like the hidden desktop's heap being too small, and it was not
// (Deskbar task 4888c75e). Nothing in the session was leaking them either, as this comment first said: four such
// runs at once fill the table, and a table that has filled once refuses names on some processors until sign-out
// (src/Deskbar.Windows/Recovery/ClassNameSlots.cs).
public class StaThreadTests
{
    [Fact]
    public void A_test_thread_gives_its_window_classes_back_even_with_a_window_left_open()
    {
        var classes = new List<WpfClass>();

        StaThread.Run(() =>
        {
            // A real window with a handle, left open on purpose: plenty of tests never close theirs.
            var window = new Window { Width = 40, Height = 40, ShowInTaskbar = false };
            new WindowInteropHelper(window).EnsureHandle();
            classes.AddRange(WpfClassesOfThisThread());
        });

        // Both the Dispatcher's message-only window and the Window's own were found, or the test proves nothing.
        Assert.True(classes.Count >= 2, $"found only {classes.Count} WPF window classes on the test thread");
        Assert.All(classes, c => Assert.False(IsRegistered(c), $"{c.Name} ({c.Kind} window) is still registered after the thread ended"));
    }

    // A WPF window class: its name, the module it was registered under (GetClassInfoExW needs both) and the kind
    // of window it was found behind.
    record WpfClass(string Name, nint Module, string Kind);

    // Every class named "HwndWrapper[...]" behind a window of this thread, top-level or message-only. The filter
    // leaves out system classes such as the thread's IME window, which belong to user32 and stay registered.
    static IReadOnlyList<WpfClass> WpfClassesOfThisThread()
    {
        var thread = GetCurrentThreadId();
        var windows = new List<(nint Hwnd, string Kind)>();
        EnumThreadWindows(thread, (hwnd, _) => { windows.Add((hwnd, "top-level")); return true; }, 0);
        // Message-only windows are not top-level, so EnumThreadWindows misses them: walk HWND_MESSAGE's children.
        for (var child = FindWindowExW(HWND_MESSAGE, 0, null, null); child != 0; child = FindWindowExW(HWND_MESSAGE, child, null, null))
            if (GetWindowThreadProcessId(child, out _) == thread) windows.Add((child, "message-only"));

        // The kind is kept so a failure says which of the thread's windows kept its class.
        return windows
            .Select(w => new WpfClass(ClassNameOf(w.Hwnd), GetClassLongPtrW(w.Hwnd, GCLP_HMODULE), w.Kind))
            .Where(c => c.Name.StartsWith("HwndWrapper[", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    static string ClassNameOf(nint hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    static bool IsRegistered(WpfClass c)
    {
        var info = new WNDCLASSEXW { cbSize = Marshal.SizeOf<WNDCLASSEXW>() };
        return GetClassInfoExW(c.Module, c.Name, ref info);
    }

    const int GCLP_HMODULE = -16;
    static readonly nint HWND_MESSAGE = -3;

    // The two names are pointers, never `string`: GetClassInfoExW writes its own pointers into them, and a string
    // field makes the marshaler free those on the way back, which corrupted the heap and crashed testhost on CI.
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public nint lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    static extern nint GetClassLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetClassInfoExW(nint instance, string className, ref WNDCLASSEXW info);
}
