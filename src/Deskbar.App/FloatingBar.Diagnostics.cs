using System.Text;
using CSharpFunctionalExtensions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

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

    // The bar's half of the dump's "Active window" section (DebugDump.DescribeActiveWindow says what it is for): how
    // WPF renders here, and every icon that carries a focus marker or belongs to the window the manager has marked,
    // with what was actually painted on it. Read from iconRings, which RebuildCore refills on every rebuild, so this
    // is the bar as it stands, not as it was built the first time.
    internal string DescribeMarkersForDump()
    {
        var text = new StringBuilder();
        void Line(string name, object? value) => text.AppendLine($"  {name,-26} {value}");

        // Tier 0 is software rendering for the whole process; RenderMode says the same for the bar's own window.
        // Both, because a remote session can be either and the marker's glow is an effect.
        var source = PresentationSource.FromVisual(this) as HwndSource;
        Line("render tier", $"{RenderCapability.Tier >> 16}  (0 = software)  bar render mode {source?.CompositionTarget?.RenderMode.ToString() ?? "?"}");

        var active = manager.ActiveWindow;
        var marked = iconRings
            .Where(i => i.IsActive || active.Map(h => h == i.Handle).GetValueOrDefault(false))
            .ToList();
        Line("icons on the bar", $"{iconRings.Count}, {iconRings.Count(i => i.IsActive)} drawn as focused, current row {currentRow?.ToString()[..8] ?? "none"}");
        if (marked.Count == 0) text.AppendLine("  no icon is drawn as focused, and none belongs to the manager's active window");
        marked.ForEach(i => text.AppendLine(
            $"  icon 0x{i.Handle.Value:X} row {i.RowKey?.ToString()[..8] ?? "(no workspace)"}  focused={i.IsActive} landing={i.WillActivate}"
            + $"  border {i.Button.BorderBrush} {i.Button.BorderThickness}  fill {i.Button.Background}  glow {GetMarkerGlow(i.Button):0.##}"
            + $"  bar {GetMarkerBar(i.Button)?.ToString() ?? "none"}  visible={i.Button.IsVisible} size {i.Button.ActualWidth:0.#}x{i.Button.ActualHeight:0.#}"));
        return text.ToString();
    }

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
        Line("strip held by work area", DescribeStripForDump());
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
