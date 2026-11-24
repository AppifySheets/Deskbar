using System.Globalization;
using System.IO;

namespace Deskbar.App;

// THE LEDGER OF COLOURED BORDERS: which windows this copy has given a focus ring through Windows' own border
// (DWMWA_BORDER_COLOR, see WindowRings), kept in a small file so the NEXT copy can put back a border this one could not.
//
// Why it exists. Petre's screenshot, "two windows have a solid line": the Windows Terminal window he was in had the
// workspace's ring, and the Edge window beside it had the same solid colour although nothing had focused it for ten
// minutes. The trace showed the 2.5.396 copy setting that border at 00:13:31, then handing over to the 2.5.399 update
// at 00:15:08 without a single "cleared" line: the update hand-over skipped WindowRings.Dispose (fixed in
// App.DownloadAndRestart, which now leaves through ExitApp). The 2.5.399 copy knew nothing of the border and never touched
// it, so it stayed until the window closed.
//
// That gap is closed, but a copy can still go without its tidy exit: killed (a developer's build swap, the hang
// watchdog in App.EndIfShutdownHangs) or crashed. A border is a property of the OTHER program's window, so it outlives
// us; a RingFrame is our own window and goes with our process, which is why only borders are listed here. The next
// copy reads this file on start, puts back every border still standing, and deletes it.
//
// One line per window: the handle in hex and the id of the process that owned it when the border was set, e.g.
// "0x9DA1022 4321". The process id is the guard against a reused handle: a window handle is recycled once its window
// closes, and resetting the border of some other program's window would undo a colour that program chose.
static class RingBorderLedger
{
    public sealed record Entry(nint Window, uint Process);

    // Beside state.json, in the state folder, so a Debug build run with DESKBAR_APPDATA keeps its own (CLAUDE.md,
    // Traps).
    public static string DefaultPath => Path.Combine(AppPaths.NewStateDir, "ring-borders.txt");

    public static string Format(IEnumerable<Entry> entries) =>
        string.Concat(entries.Select(e => $"0x{e.Window:X} {e.Process}\n"));

    // Tolerant on purpose: a line that does not read is skipped, never thrown over. A start must not fail because a
    // killed copy left half a line behind.
    public static IReadOnlyList<Entry> Parse(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            .Select(parts => long.TryParse(parts[0][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var window)
                             && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var process)
                ? new Entry((nint)window, process)
                : null)
            .OfType<Entry>()
            .ToList();

    // The windows whose border should be put back: still a window, and still owned by the process that owned it when
    // the border was set (see the header for why both).
    public static IReadOnlyList<nint> Leftovers(IEnumerable<Entry> entries, Func<nint, bool> isWindow, Func<nint, uint> processOf) =>
        entries.Where(e => isWindow(e.Window) && processOf(e.Window) == e.Process).Select(e => e.Window).ToList();
}
