using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deskbar.App;

// ONE PINNED WORKSPACE'S BUTTON ON WINDOWS' TASKBAR. Petre: "can it also show up in windows taskbar?", then "yes and also
// do the taskbar". Owned by TaskbarJumps, one per pinned workspace; what it reads and where it goes is the same
// WorkspaceJump the strip items draw (FloatingBar.WorkspaceJumps.cs).
//
// A window that is nothing but its taskbar button, the way BarStandIn is for the minimized bar, and built on what the
// spike measured (docs/superpowers/notes/2026-11-20-taskbar-jump-spike.md):
//
//   * IT LIVES MINIMIZED. A click on the button of the ACTIVE window minimizes it, straight from the shell and with no
//     syscommand to refuse (finding 3), so a button that could ever be active could lose a click. A minimized window's
//     click is always a restore, so every click arrives here as a state change away from Minimized: that is the click.
//     The window goes straight back to Minimized, and the switch runs after, from the dispatcher.
//   * Its own AppUserModelID, so each pinned workspace is its own button rather than one group (finding 1).
//   * Parked off every screen and tiny, so the restore a click causes shows nothing (finding 4).
//
// The title is the item's label ("Messengers", or "↩ Deskbar" inside it), which is what the button's hover shows, and
// the icon is a rounded square in the colour of where a click goes, with the workspace's initial or the back arrow:
// the mockup's taskbar button.
public sealed class TaskbarJumpWindow : Window
{
    // A click on the button. Raised from the dispatcher after the window is minimized again.
    public event Action? Clicked;

    // What the icon was last drawn for, so a rebuild that changes nothing does not redraw it.
    (string Glyph, Color Colour)? drawn;

    public TaskbarJumpWindow()
    {
        ShowInTaskbar = true;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
    }

    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    // Shown normal off screen first, then minimized: a window must be on a desktop before it can be pinned to all of
    // them (BarStandIn's lesson), so TaskbarJumps pins between Show and this.
    public void MinimizeToButton()
    {
        WindowState = WindowState.Minimized;
        armed = true;
    }

    bool armed;

    // The click (see the header). Re-minimizing raises this again with Minimized, which returns at once.
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!armed || WindowState == WindowState.Minimized) return;
        WindowState = WindowState.Minimized;
        Click();
    }

    // An ACTIVATION is a click too (final review, Important 3b): Alt+Tab reaches a minimized window without changing its
    // state, as BarStandIn's comment found, so without this, picking the button in Alt+Tab would leave the focus on an
    // invisible window and switch nowhere. A taskbar click raises an activation AND a restore (spike log: WM_ACTIVATE
    // first, then WM_SIZE), and Click below makes the two one click.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (armed) Activation();
    }

    internal void Activation()
    {
        if (WindowState != WindowState.Minimized) WindowState = WindowState.Minimized;
        Click();
    }

    // One click per gesture: the activation and the restore of one taskbar click arrive about a millisecond apart, a
    // person's second click no sooner than a few hundred. The switch runs from the dispatcher, after the window is
    // minimized again.
    long lastClick = long.MinValue / 2;
    const long OneClickMs = 400;

    void Click()
    {
        var now = Environment.TickCount64;
        if (now - lastClick < OneClickMs) return;
        lastClick = now;
        Dispatcher.BeginInvoke(() => Clicked?.Invoke());
    }

    // Closed only by TaskbarJumps (CloseForGood), when its workspace is unpinned. The thumbnail's X and the button's
    // "Close window" are refused (final review, Important 3a): closing would take the button away until the workspace
    // was unpinned and pinned again. App shutdown is not refused: WPF ignores Cancel while the application shuts down.
    bool closingForGood;

    internal void CloseForGood()
    {
        closingForGood = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!closingForGood) e.Cancel = true;
    }

    // The label as the button's title and the icon for it, redrawn only when glyph or colour changed.
    public void Show(string label, string glyph, Color colour)
    {
        Title = label;
        if (drawn == (glyph, colour)) return;
        drawn = (glyph, colour);
        Icon = IconFor(glyph, colour);
    }

    // A 32 px rounded square in `colour` with `glyph` centred on it, in white or near black, whichever reads on that
    // colour (the same luminance split the strip's name tag text would need).
    static ImageSource IconFor(string glyph, Color colour)
    {
        const int size = 32;
        var luminance = (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255;
        var ink = luminance > 0.6 ? Color.FromRgb(0x0B, 0x0C, 0x0E) : Colors.White;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(colour);
            fill.Freeze();
            context.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), 6, 6);
            var inkBrush = new SolidColorBrush(ink);
            inkBrush.Freeze();
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 20, inkBrush, 1.0);
            context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
