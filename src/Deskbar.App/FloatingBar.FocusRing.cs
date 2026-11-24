using System.Windows.Threading;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// The focus ring's hook in the bar (FocusRings decides, WindowRings draws). Here because the bar already has both
// things the rings need, worked out once per rebuild: the overview, which says which window has focus and which is in
// front on each screen, and the bar's colour rules (RowColour), so a ring is the exact colour of its workspace's lane.
//
// It also drives the BLINKING BORDER of a window asking for you (WorkspaceManager.seenAt, FocusRings.WithAsking):
// Petre's "a blinking border for that window that's asking for attention", option "a" of the mockup at
// https://claude.ai/artifact/PPhYsVsC8x8647K3MAjF4m. The blink is the ring's colour flipping on a timer, so it rides
// Windows' own border (or the ring's frame on a maximized window) and needs nothing drawn of its own.
public partial class FloatingBar
{
    // Set by App. Null in the tests that build a bar on its own, and in compatibility mode.
    internal WindowRings? Rings { get; init; }

    // How long ago the last keyboard or mouse input was (GetLastInputInfo, InputActivity), for SettleAsks: whether
    // somebody is here to see a blink, and whether they have clicked or typed in the blinking window. Set by App; left
    // null, nobody is ever "here", so nothing blinks and marks clear only by focus.
    internal Func<TimeSpan>? SinceLastInput { get; init; }

    // The beat: the asking row's own half cycle (AskingHalfCycle, 600 ms), so the border and the row blink together.
    DispatcherTimer? askingBeat;
    bool askingLit;

    // The overview the rings were last worked out from, re-used by the beat between rebuilds.
    Overview? ringOverview;

    // Called by both rebuilds, the full one and the strips-only one of a minimized bar (FillStripsAlone), so the rings
    // follow focus whether the bar is showing or not. With the setting off, every focus ring is taken away, and only a
    // blinking window's border is drawn.
    //
    // DRAWN AFTER THE REBUILD, NEVER INSIDE IT (task c1640d1f, Petre: "selecting a window flashes the entire bar so that
    // everything blinks"). A ring's frame is a layered WPF window on this thread, and placing it resizes it. WPF answers a
    // layered window's resize by rendering EVERY window of the thread on the spot (HwndTarget.OnResize calls
    // MediaContext.Resize, which runs the whole render pass, then CompleteRender; read in dotnet/wpf). Drawn from inside a
    // rebuild, that painted the bar half way through it: the trace put a "frame was drawn mid-rebuild" line between the
    // ring's border and frame lines on every switch, and a screen probe caught the bar's rows and their veil missing
    // for the 140 to 170 ms the rest of the rebuild took. So the rebuild only notes the overview, and the rings are drawn
    // once it is over, from the dispatcher at Loaded priority: after the new rows have been laid out and rendered. A
    // burst of rebuilds draws once, from the newest overview.
    void ShowFocusRings(Overview overview)
    {
        ringOverview = overview;
        if (ringsQueued) return;
        ringsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ringsQueued = false;
            RunAskingBeat();
            DrawRings();
        });
    }

    bool ringsQueued;

    // A seam for the tests and nothing else: told, each time the rings are drawn, whether a rebuild was running then.
    internal Action<bool>? RingsDrawn { get; set; }

    void DrawRings()
    {
        if (ringOverview is not { } overview) return;
        RingsDrawn?.Invoke(rebuilding);
        var on = manager.State.FocusRing;
        var rings = on ? FocusRings.Of(overview, w => RingColourOf(overview, w)) : [];
        Rings?.Show(FocusRings.WithAsking(rings, overview, manager.BlinkingWindows, askingLit, on ? FocusRings.DimWhite : null));
    }

    // The timer runs while any window carries a mark (HasWindowMarks) or still blinks, and stops by itself after: no
    // tick at all on an ordinary day. Each tick lets the manager start and end blinks (SettleAsks), then flips the beat.
    void RunAskingBeat()
    {
        var wanted = Rings is not null && (manager.HasWindowMarks || manager.BlinkingWindows.Count > 0);
        if (askingBeat is null)
        {
            if (!wanted) return;
            askingBeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = AskingHalfCycle.TimeSpan };
            askingBeat.Tick += (_, _) => AskingTick();
            Closed += (_, _) => askingBeat.Stop();
        }
        if (wanted && !askingBeat.IsEnabled)
        {
            // Lit first, so a blink shows the moment it starts.
            askingLit = true;
            askingBeat.Start();
        }
        else if (!wanted && askingBeat.IsEnabled)
        {
            askingBeat.Stop();
            askingLit = false;
        }
    }

    void AskingTick()
    {
        // Settling pulses StateChanged when a blink starts or ends, and the rebuild that follows calls ShowFocusRings
        // (which may stop this timer), so the beat is flipped and drawn only after it.
        manager.SettleAsks(SinceLastInput?.Invoke() ?? TimeSpan.MaxValue);
        askingLit = !askingLit;
        if (askingBeat?.IsEnabled == true && manager.BlinkingWindows.Count > 0) DrawRings();
    }

    // A workspace's colour as the ring wants it: #RRGGBB, or null where the workspace's colour is none. The same
    // resolution as the window list's spine and a strip's tag (WorkspaceAccent, ListSpine): the group's colour in the
    // member's shade, or the palette's colour for the row's position.
    string? RingColourOf(Overview overview, WorkspaceGroup workspace)
    {
        var group = workspace.Workspace.GroupId is { } id ? manager.State.Groups.FirstOrDefault(g => g.Id == id) : null;
        var (colour, slot, _) = RowColour(overview, workspace, group);
        return WorkspacePalette.IsNone(colour) ? null : WorkspacePalette.For(colour, slot < 0 ? 0 : slot);
    }
}
