using System.Reactive;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Deskbar.Core.Tiling;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// ONE TILE'S LOCK MARK: a padlock laid over the tile's title bar, just left of its minimize button. Petre: "can you have
// extra buttons on the windows themselves ... so i don't have to go to the bar?", "can you also draw the pin on the
// window?", and from the glyph mockup, B, the padlock. Spec: 2026-11-22-tile-lock-and-other-screens-design.md, "The pin
// on the window". LockMarks (Core) decides where and which look; TileLockMarks keeps one of these on each tile.
//
// It is a window of Deskbar's own, never anything inside the app: a real button in another app's title bar would mean
// injecting code into its process. And it is NOT owned by the tile, because an owner across two processes joins their
// input queues and a hung app would freeze the bar; it is placed directly above the tile instead (ZOrder), so whatever
// covers the tile covers the mark too.
//
// Like RingFrame, it must never get in the way, so it is:
//   * never activated (ShowActivated false, WS_EX_NOACTIVATE): a click toggles the lock and focus stays where it was;
//   * never on the taskbar or in Alt+Tab (ShowInTaskbar false, WS_EX_TOOLWINDOW), which also keeps it out of the bar's
//     window list (TopLevelWindows takes taskbar candidates only);
// but unlike RingFrame it is NOT click-through: taking the click is its job. Its background is therefore never fully
// transparent: a layered window lets clicks through every pixel with zero alpha, so the unlocked look's "empty" square
// is filled with an alpha of 1.
//
// The two looks (spec): locked, a closed padlock in white on a #2F6FD6 square; unlocked, an open padlock and a thin
// outline in #8A8F98, which reads on a dark or a light title bar alike.
sealed class LockMarkWindow : Window
{
    // The glyph is drawn on a 24 x 24 canvas (LockMarks.SizeDip) inside a Viewbox, so it fills whatever size the
    // window is given in physical pixels, at any scale.
    static readonly Geometry ClosedShackle = Frozen(Geometry.Parse("M8,11 V8 A4,4 0 0 1 16,8 V11"));
    static readonly Geometry OpenShackle = Frozen(Geometry.Parse("M8,11 V8 A4,4 0 0 1 15.6,6.2"));
    static readonly Brush LockedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xD6)));
    static readonly Brush Glyph = Frozen(new SolidColorBrush(Colors.White));
    static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)));
    static readonly Brush ClickableClear = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    readonly Border square;
    readonly Path shackle;
    readonly Rectangle body;
    readonly Subject<Unit> clicked = new();

    public LockMarkWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        // Off screen and tiny until the first Put, so the first Show draws nothing anywhere.
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Title = "Deskbar lock mark";
        Cursor = Cursors.Hand;

        shackle = new Path { StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        body = new Rectangle { Width = 14, Height = 10, RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(body, 5);
        Canvas.SetTop(body, 11);
        var canvas = new Canvas { Width = 24, Height = 24, Children = { shackle, body } };
        square = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), Child = canvas };
        var content = new Viewbox { Stretch = Stretch.Uniform, Child = square };
        content.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            clicked.OnNext(Unit.Default);
        };
        Content = content;
        Look(locked: false);
    }

    public nint Handle => new WindowInteropHelper(this).EnsureHandle();

    // A left click released on the mark. TileLockMarks toggles the lock.
    public IObservable<Unit> Clicked => clicked;

    // Which look is showing, for the tests.
    internal bool LockedLook { get; private set; }

    // The extended styles WPF has no property for, stamped once on the new handle (see the header).
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) | (nint)(NativeMethods.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    const long WS_EX_NOACTIVATE = 0x08000000;

    // Shows `mark`'s look on `mark.Frame` (physical pixels), directly above `target`. A tile is never topmost, so neither
    // is the mark. Shown first if hidden, so WPF's idea of the window's visibility matches what SetWindowPos does.
    public void Put(LockMark mark, nint target)
    {
        if (mark.Locked != LockedLook) Look(mark.Locked);
        if (!IsVisible) Show();
        var f = mark.Frame;
        ZOrder.PlaceDirectlyAbove(Handle, target, f.Left, f.Top, f.Width, f.Height);
    }

    void Look(bool locked)
    {
        LockedLook = locked;
        square.Background = locked ? LockedFill : ClickableClear;
        square.BorderBrush = locked ? null : Outline;
        square.BorderThickness = new Thickness(locked ? 0 : 1.5);
        shackle.Data = locked ? ClosedShackle : OpenShackle;
        shackle.Stroke = locked ? Glyph : Outline;
        body.Fill = locked ? Glyph : Outline;
        ToolTip = locked ? "New windows won't split this tile. Click to allow." : "Don't split this tile";
    }

    protected override void OnClosed(EventArgs e)
    {
        clicked.OnCompleted();
        base.OnClosed(e);
    }

    // Frozen, because a static brush or geometry that is not takes the thread's affinity (CLAUDE.md, Traps).
    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
