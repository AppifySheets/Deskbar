using System.Text;
using System.Windows.Interop;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// The bar's half of the debug dump (DebugDump writes the file): what the bar believes about itself, which no
// outside view of the window can show. Its geometry as WPF holds it, how it is docked, whether the shell has
// told it a fullscreen app is up, and every time the shell has said so since it started.
//
// Written for the first report it serves: the bar sitting over a fullscreen window in an RDP session on
// another computer, where nothing can be traced live. Two explanations fit that screenshot and they need
// opposite fixes: the shell never sent ABN_FULLSCREENAPP, or it sent it and the bar stayed on top anyway.
// The notice history below says which.
public partial class FloatingBar
{
    // Every ABN_FULLSCREENAPP the shell has sent, newest last, capped so a day of videos cannot grow it
    // without end. The time is local, to match the trace log's.
    readonly List<(DateTime At, bool Open)> fullScreenNotices = [];

    const int FullScreenNoticesKept = 20;

    void RememberFullScreenNotice(bool open)
    {
        fullScreenNotices.Add((DateTime.Now, open));
        if (fullScreenNotices.Count > FullScreenNoticesKept) fullScreenNotices.RemoveAt(0);
        ClickTrace.Write($"fullscreen app {(open ? "opened" : "closed")} (shell notice), docked={dockedEdge ?? "no"}");
    }

    // The bar's window handle, for the dump to find the bar in the z-order. Zero before the window exists.
    internal nint DumpHandle => new WindowInteropHelper(this).Handle;

    // Plain lines, one fact each, in the order someone reading the dump asks about them.
    internal string DescribeForDump()
    {
        var hwnd = DumpHandle;
        var text = new StringBuilder();
        void Line(string name, object? value) => text.AppendLine($"  {name,-26} {value}");

        Line("hwnd", $"0x{hwnd:X}");
        Line("visible", IsVisible);
        Line("minimized", Minimized);
        Line("being dragged", moving);
        Line("docked edge", dockedEdge ?? "(floating)");
        Line("dock setting", $"DockBar={manager.State.DockBar} DockAcrossScreens={manager.State.DockAcrossScreens}");
        Line("app bar registered", $"main={appBar?.Registered.ToString() ?? "never created"} seam={seamBar?.Registered.ToString() ?? "never created"}");
        Line("fullscreen app open", $"{fullScreenAppOpen} (set only by the shell's notice, and only while docked)");
        Line("WPF geometry (DIPs)", $"Left={Left:0.#} Top={Top:0.#} Width={ActualWidth:0.#} Height={ActualHeight:0.#}");
        Line("anchors", $"right={anchorRight?.ToString("0.#") ?? "none"} bottom={anchorBottom?.ToString("0.#") ?? "none"}");

        if (hwnd != 0)
        {
            NativeMethods.GetWindowRect(hwnd, out var rect);
            Line("window rect (px)", DebugDump.Format(rect));
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            Line("topmost now", (exStyle & DebugDump.WS_EX_TOPMOST) != 0);
        }

        text.AppendLine(fullScreenNotices.Count == 0
            ? "  fullscreen notices         none since the bar started"
            : $"  fullscreen notices         the last {fullScreenNotices.Count}, oldest first:");
        fullScreenNotices.ForEach(n => text.AppendLine($"    {n.At:yyyy-MM-dd HH:mm:ss.fff}  {(n.Open ? "opened" : "closed")}"));
        return text.ToString();
    }
}
