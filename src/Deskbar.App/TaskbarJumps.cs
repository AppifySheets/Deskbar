using System.Windows;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// THE PINNED WORKSPACES' BUTTONS ON WINDOWS' TASKBAR: one TaskbarJumpWindow per pinned workspace, kept in step with the
// items the bar draws (FloatingBar.WorkspaceJumps.cs calls Show from the same rebuild). Petre: "yes and also do the
// taskbar". Design: docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md; measurements:
// docs/superpowers/notes/2026-11-20-taskbar-jump-spike.md.
//
// A window is KEPT by its pinned workspace's id and retitled when its item changes, never recreated, because a new
// window's button goes to the far end of the taskbar and a button that jumps around is the design's anti-goal ("the
// item moving under the pointer"). A workspace that is no longer pinned has its window closed.
//
// The three effects are passed in, so a test can record them instead of changing the machine:
//   pin       Windows' "show on all desktops", so the button is on every desktop's taskbar (IVirtualDesktopService.Pin,
//             not WorkspaceManager.PinWindow: that also remembers the pin by identity, and these are Deskbar's own
//             windows, which placement memory must never learn)
//   ignore    the window monitor's ignore list, BEFORE the window is shown or pinned, or it would be listed as a pinned
//             window on every row of the bar (the spike's probe windows were)
//   switchTo  the click: WorkspaceManager.Switch, the path the bar's rows use
public sealed class TaskbarJumps(Func<WindowHandle, Result> pin, Action<nint> ignore, Func<Guid, Result> switchTo)
{
    readonly Dictionary<Guid, TaskbarJumpWindow> windows = [];

    // Where each window's click goes now; rewritten by every Show, read when the click arrives.
    readonly Dictionary<Guid, Maybe<Guid>> targets = [];

    // The windows, in the order of the jumps last shown. For tests.
    internal IReadOnlyList<TaskbarJumpWindow> Windows => [.. windows.Values];

    // Brings the buttons in line with `jumps`: one per jump, titled and drawn as the item reads, in `colourOf` its
    // destination's colour.
    public void Show(IReadOnlyList<WorkspaceJump> jumps, Func<WorkspaceJump, Brush> colourOf)
    {
        var wanted = jumps.Select(j => j.PinnedId).ToHashSet();
        windows.Keys.Where(id => !wanted.Contains(id)).ToList().ForEach(Remove);

        jumps.ToList().ForEach(jump =>
        {
            targets[jump.PinnedId] = jump.Target;
            var window = windows.TryGetValue(jump.PinnedId, out var existing) ? existing : Create(jump.PinnedId);
            window.Show(jump.Label, GlyphOf(jump), (colourOf(jump) as SolidColorBrush)?.Color ?? Colors.SlateGray);
        });
    }

    // Closes every button; the app's exit.
    public void Close() => windows.Keys.ToList().ForEach(Remove);

    // The icon's glyph: the back arrow when the click goes back, else the workspace's initial.
    static string GlyphOf(WorkspaceJump jump) =>
        jump.IsBack ? WorkspaceJumps.BackGlyph : jump.Label.Trim() is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "•";

    TaskbarJumpWindow Create(Guid pinnedId)
    {
        var window = new TaskbarJumpWindow();
        // Registered FIRST: Show and the pin below are COM and window calls that pump messages, so a rebuild can run
        // inside them and call Show again; finding the window already here is what stops it making a second one.
        windows[pinnedId] = window;
        var hwnd = window.EnsureHandle();
        ignore(hwnd);
        // Its own button, not one shared with every other Deskbar window (spike, finding 1). A failure leaves the
        // button grouped with Deskbar's other windows, which still works, so it is traced and survived.
        WindowAppId.Set(hwnd, $"Deskbar.Jump.{pinnedId:N}").TapError(error => ClickTrace.Write($"taskbar jump app id failed: {error}"));
        window.Clicked += () => targets.GetValueOrDefault(pinnedId).Tap(target =>
        {
            switchTo(target).TapError(error => ClickTrace.Write($"taskbar jump to {target} failed: {error}"));
        });
        window.Show();
        // On every desktop's taskbar. A failure leaves the button on this desktop only, so it is traced and survived,
        // as the bar's stand-in does.
        pin(new WindowHandle(hwnd)).TapError(error => ClickTrace.Write($"taskbar jump pin failed: {error}"));
        window.MinimizeToButton();
        return window;
    }

    void Remove(Guid pinnedId)
    {
        if (!windows.Remove(pinnedId, out var window)) return;
        targets.Remove(pinnedId);
        window.Close();
    }
}
