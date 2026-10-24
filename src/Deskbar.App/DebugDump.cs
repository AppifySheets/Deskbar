using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Windows.Monitoring;
using Deskbar.Windows.Processes;

namespace Deskbar.App;

// "Save debug dump", on the tray menu, the bar's background menu and `deskbar.cmd dump`. Petre, about the bar
// sitting over a fullscreen window in an RDP session on another computer: "let's add debug dump which will
// give me a file and open window".
//
// The trace log (ClickTrace) answers questions about a sequence and has to be switched on BEFORE the problem
// happens. This answers questions about a MOMENT, on a machine nobody here can probe: what the screens are,
// where the shell keeps the taskbar, what the bar believes about itself, and every visible top-level window in
// z-order with the facts a z-order or work-area problem turns on (topmost, maximized, caption, whether it
// covers its whole screen as a fullscreen window does). One file, plain text, so it can be pasted
// into a chat or attached to an issue as it is.
//
// Every section is written on its own and a section that throws says so in place of its body, so one
// failed call (a process that will not open, a shell that does not answer) cannot cost the rest. All
// geometry is in PHYSICAL pixels: the app is per-monitor DPI aware (2.1.22), and the header records the
// awareness actually in effect, because an unaware thread is handed virtualised numbers that look fine.
//
// Saved under %APPDATA%\Deskbar\dumps rather than %TEMP%, so it is easy to find again on a machine that is
// not one's own; the menu items then open Explorer with the file selected (Reveal). Titles of every window
// are in the file, which is the point of it, and also why it is written only when asked.
static class DebugDump
{
    public static Result<string> Save(FloatingBar? bar, WorkspaceManager manager) =>
        Result.Try(() =>
        {
            var folder = Path.Combine(AppPaths.NewStateDir, "dumps");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"deskbar-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Compose(bar, manager));
            ClickTrace.Write($"debug dump written: {path}");
            return path;
        }, e => $"Could not write the debug dump: {e.Message}");

    // Explorer with the file selected: the "open window" half of the request, and the quickest way to copy the
    // file off a remote machine.
    //
    // Through DetachedLauncher, so under the Store copy the Explorer window is not part of Deskbar's package
    // (store packaging design). Explorer's own `/select,"<path>"` goes as ONE verbatim string with the path
    // always quoted, as before the package; split into two C-runtime arguments the path went unquoted when it
    // had no space, and a comma in it broke Explorer's parse (TaskbarPins.Reveal has the same fix).
    public static Result Reveal(string path) =>
        DetachedLauncher.StartVerbatim("explorer.exe", $"/select,\"{path}\"", null)
            .MapError(e => $"The dump is at {path}, but Explorer could not be opened: {e}");

    internal static string Compose(FloatingBar? bar, WorkspaceManager manager)
    {
        var text = new StringBuilder();
        // Read once and shared: the window list names each window's screen by the same numbers.
        var screens = Result.Try(Screens).GetValueOrDefault([]);
        Section(text, "Deskbar debug dump", Header);
        Section(text, "Screens (physical pixels; work = what is left after the taskbar and docked bars)", () => DescribeScreens(screens));
        Section(text, "Windows taskbar, as the shell reports it", DescribeTaskbar);
        Section(text, "The bar", () => bar?.DescribeForDump() ?? "  no bar (compatibility mode)\n");
        Section(text, "Visible top-level windows, top of the z-order first", () => DescribeZOrder(screens, bar?.DumpHandle ?? 0));
        Section(text, "Workspaces", () => DescribeWorkspaces(manager));
        Section(text, $"Trace log, last {TraceLines} lines ({ClickTrace.LogPath})", TraceTail);
        return text.ToString();
    }

    // One section: a heading, then the body, or the reason the body could not be written.
    static void Section(StringBuilder text, string title, Func<string> body)
    {
        text.AppendLine($"== {title}");
        try { text.Append(body()); }
        catch (Exception ex) { text.AppendLine($"  (this section failed: {ex.GetType().Name}: {ex.Message})"); }
        text.AppendLine();
    }

    // --- header -------------------------------------------------------------------------------------------

    static string Header()
    {
        var text = new StringBuilder();
        void Line(string name, object? value) => text.AppendLine($"  {name,-26} {value}");

        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetCursorPos(out var cursor);
        var notification = SHQueryUserNotificationState(out var state) == 0 ? NotificationStateName(state) : "(no answer)";

        Line("written", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Line("version", UpdateService.RunningVersion);
        // Pro or the free tier, and why: the cached verdict (Entitlement), so the dump never waits on the Store.
        Line("entitlement", $"{(Entitlement.Current.IsPro ? "Pro" : "free")}, {Entitlement.Current.Reason}");
        Line("exe", Environment.ProcessPath);
        Line("windows", $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
        Line("session", $"id {Process.GetCurrentProcess().SessionId}, console session {WTSGetActiveConsoleSessionId()}, "
            + $"remote (RDP) {GetSystemMetrics(SM_REMOTESESSION) != 0}");
        Line("dpi awareness", DpiAwarenessName(GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())));
        Line("user notification state", $"{notification}  (QUNS_BUSY or D3D_FULL_SCREEN means Windows thinks a fullscreen app is up)");
        Line("foreground window", foreground == 0 ? "none" : $"0x{foreground:X}  {TitleOf(foreground)}");
        Line("cursor", $"{cursor.X},{cursor.Y}");
        return text.ToString();
    }

    static string NotificationStateName(int state) => state switch
    {
        1 => "QUNS_NOT_PRESENT (screen saver, locked, or a fast user switch)",
        2 => "QUNS_BUSY (a fullscreen app, or presentation settings)",
        3 => "QUNS_RUNNING_D3D_FULL_SCREEN",
        4 => "QUNS_PRESENTATION_MODE",
        5 => "QUNS_ACCEPTS_NOTIFICATIONS (nothing fullscreen)",
        6 => "QUNS_QUIET_TIME",
        7 => "QUNS_APP (a Windows Store app is fullscreen)",
        _ => $"unknown ({state})",
    };

    static string DpiAwarenessName(int awareness) => awareness switch
    {
        0 => "unaware (rectangles below are VIRTUALISED and wrong)",
        1 => "system aware (rectangles on a screen at another scale are virtualised)",
        2 => "per-monitor aware",
        _ => $"unknown ({awareness})",
    };

    // --- screens ------------------------------------------------------------------------------------------

    sealed record Screen(int Number, nint Handle, NativeMethods.RECT Bounds, NativeMethods.RECT Work, bool Primary, string Device, uint Dpi);

    static IReadOnlyList<Screen> Screens()
    {
        var handles = new List<nint>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeMethods.RECT _, nint _) =>
        {
            handles.Add(monitor);
            return true;
        }, 0);
        return handles.Select((handle, at) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            NativeMethods.GetMonitorInfoEx(handle, ref info);
            NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _);
            return new Screen(at + 1, handle, info.rcMonitor, info.rcWork, (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, info.szDevice, dpi);
        }).ToList();
    }

    static string DescribeScreens(IReadOnlyList<Screen> screens) =>
        screens.Count == 0
            ? "  none enumerated\n"
            : string.Concat(screens.Select(s =>
                $"  screen {s.Number}  {s.Device}{(s.Primary ? "  primary" : "")}  dpi {s.Dpi} ({s.Dpi * 100 / 96}%)\n"
                + $"    bounds {Format(s.Bounds)}\n"
                + $"    work   {Format(s.Work)}\n"));

    // --- taskbar ------------------------------------------------------------------------------------------

    static string DescribeTaskbar() =>
        AppBar.Taskbar() is { } taskbar
            ? $"  {Format(new NativeMethods.RECT { Left = taskbar.Rect.L, Top = taskbar.Rect.T, Right = taskbar.Rect.R, Bottom = taskbar.Rect.B })}"
              + $"  edge {(taskbar.Edge switch { 0 => "left", 1 => "top", 2 => "right", 3 => "bottom", var e => e.ToString() })}"
              + $"  auto-hide {taskbar.AutoHide}\n"
            : "  the shell did not answer (no taskbar in this session?)\n";

    // --- windows ------------------------------------------------------------------------------------------

    // EnumWindows walks top-level windows in z-order, top first, which is the order that matters here: every
    // window listed above the bar's line is drawn over it.
    static string DescribeZOrder(IReadOnlyList<Screen> screens, nint barHwnd)
    {
        var all = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) => { all.Add(hwnd); return true; }, 0);
        var foreground = NativeMethods.GetForegroundWindow();
        var processNames = new Dictionary<uint, string>();
        var visible = all.Where(NativeMethods.IsWindowVisible).Take(MaxWindows).ToList();

        var text = new StringBuilder();
        text.AppendLine($"  {all.Count} top-level windows, {visible.Count} visible listed. Flags: T topmost, MAX maximized, MIN minimized,");
        text.AppendLine("  CAP has a caption, TOOL tool window, NOACT no-activate, CLOAKED cloaked (another desktop, or hidden by DWM),");
        text.AppendLine("  COVERS not maximized, yet its rectangle covers its whole screen (a fullscreen window). Rect is GetWindowRect; frame is");
        text.AppendLine("  what DWM draws (without the invisible resize borders).");
        visible.Select((hwnd, at) => DescribeWindow(at + 1, hwnd, screens, processNames, hwnd == barHwnd, hwnd == foreground))
            .ToList()
            .ForEach(line => text.AppendLine(line));
        return text.ToString();
    }

    const int MaxWindows = 400;

    static string DescribeWindow(int position, nint hwnd, IReadOnlyList<Screen> screens, Dictionary<uint, string> processNames, bool isBar, bool isForeground)
    {
        var style = (long)NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE);
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var frame = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT bounds, Marshal.SizeOf<NativeMethods.RECT>()) == 0
            ? Format(bounds) : "?";
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var screen = screens.FirstOrDefault(s => s.Handle == monitor);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        var flags = new[]
        {
            (exStyle & WS_EX_TOPMOST) != 0 ? "T" : null,
            NativeMethods.IsZoomed(hwnd) ? "MAX" : null,
            NativeMethods.IsIconic(hwnd) ? "MIN" : null,
            (style & WS_CAPTION) == WS_CAPTION ? "CAP" : null,
            (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 ? "TOOL" : null,
            (exStyle & WS_EX_NOACTIVATE) != 0 ? "NOACT" : null,
            cloaked != 0 ? "CLOAKED" : null,
            // Not for a maximized window: its invisible resize borders reach past every edge of a screen with no
            // taskbar on it, so every maximized window there would be flagged. Measured on the first dump.
            screen is not null && !NativeMethods.IsZoomed(hwnd) && CoversScreen(rect, screen.Bounds) ? "COVERS" : null,
            isBar ? "<<< THE BAR" : null,
            isForeground ? "<<< FOREGROUND" : null,
        }.Where(f => f is not null);

        return $"  {position,3} 0x{hwnd:X8} [{string.Join(' ', flags)}] screen {screen?.Number.ToString() ?? "?"}  {ProcessName(pid, processNames)}  "
            + $"class \"{ClassOf(hwnd)}\"  \"{TitleOf(hwnd)}\"\n"
            + $"        rect {Format(rect)}  frame {frame}";
    }

    // The usual test for a fullscreen window: its rectangle reaches every edge of its screen. The shell's own rule
    // for ABN_FULLSCREENAPP is undocumented, so this is the dump's reading, not the shell's; the caller leaves
    // maximized windows out (see DescribeWindow).
    internal static bool CoversScreen(NativeMethods.RECT window, NativeMethods.RECT screen) =>
        window.Left <= screen.Left && window.Top <= screen.Top && window.Right >= screen.Right && window.Bottom >= screen.Bottom;

    static string ProcessName(uint pid, Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        string name;
        try { using var process = Process.GetProcessById((int)pid); name = $"{process.ProcessName} (pid {pid}, session {process.SessionId}{StartedOf(process)})"; }
        catch { name = $"pid {pid}"; }
        return cache[pid] = name;
    }

    // When the process started: an Explorer younger than Deskbar means the shell restarted under the bar and
    // forgot its app bar (the first RDP dump could not show this). Empty where access is refused.
    static string StartedOf(Process process)
    {
        try { return $", started {process.StartTime:MM-dd HH:mm:ss}"; }
        catch { return ""; }
    }

    static string ClassOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    static string TitleOf(nint hwnd)
    {
        var text = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    // --- workspaces and the trace log --------------------------------------------------------------------

    static string DescribeWorkspaces(WorkspaceManager manager) =>
        $"  {manager.State.Workspaces.Count} workspaces, {manager.KnownWindows.Count()} windows known\n"
        + string.Concat(manager.State.Workspaces.Select(w =>
            $"  {(manager.CurrentWorkspaceId == w.Id ? "*" : " ")} {w.Name}  desktop {w.DesktopId?.ToString() ?? "none"}\n"));

    const int TraceLines = 200;

    // Shared read, because the trace may be appending to the file at the same moment.
    static string TraceTail()
    {
        if (!File.Exists(ClickTrace.LogPath)) return $"  no trace log (tracing is {(ClickTrace.On ? "on, nothing written yet" : "off")})\n";
        using var stream = new FileStream(ClickTrace.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > TraceLines) lines.Dequeue();
        }
        return $"  tracing is {(ClickTrace.On ? "on" : "off")}\n" + string.Concat(lines.Select(l => $"  {l}\n"));
    }

    internal static string Format(NativeMethods.RECT r) =>
        $"{r.Left},{r.Top} to {r.Right},{r.Bottom} ({r.Right - r.Left}x{r.Bottom - r.Top})";

    // --- interop used only here ---------------------------------------------------------------------------

    const int GWL_STYLE = -16;
    internal const long WS_EX_TOPMOST = 0x00000008;
    const long WS_CAPTION = 0x00C00000, WS_EX_NOACTIVATE = 0x08000000;
    const int SM_REMOTESESSION = 0x1000;
    const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out NativeMethods.RECT value, int size);
}
