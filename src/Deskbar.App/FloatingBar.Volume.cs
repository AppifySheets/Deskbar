using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Audio;

namespace Deskbar.App;

// THE VOLUME DIAL in the taskbar strip. Petre: "add volume as a scrollable wheel, with a knob increasing and
// decreasing as i hover my mouse on top of it, looking like a progress bar until i hover over it", then "c"
// from three mockups: the level as an arc around the speaker, like the scale round a knob.
//
//   at rest   a thin 270 degree arc, filled to the level, round a small speaker
//   hovered   the arc thickens, a knob appears at the level, the dial grows a little, and the speaker gives
//             way to the percentage
//   wheel     2% a notch, Windows' own step, and moving it unmutes, as the keyboard's volume keys do
//   click     mute and unmute
//
// The level comes from Windows (IVolumeControl, SystemVolume in production), and so does every change made
// elsewhere: the volume keys, Windows' flyout, an app, a headset taking over as the default device.
public partial class FloatingBar
{
    // The dial's scale: 270 degrees, from bottom left round the top to bottom right, on a 32 DIP square.
    // Angles are clockwise from three o'clock, screen style, so 135 is bottom left.
    //
    // 32 rather than the first 24, and the radius with it (10 of 24, the same share of 32). Petre, on the strip:
    // "these are kinda small", then "c" from three mockups, the size closest to Windows' own tray. The stroke
    // stays 2 DIP (3.2 hovered) at the new size, so the ring gets bigger without getting heavier.
    //
    // Then 28, with the rest of the strip, to make room for the language switcher: "make those icons a bit
    // smaller", "so it fits in this" (see StripGlyphSize). The radius keeps its share of the square.
    internal const double DialSize = 28, DialCentre = DialSize / 2, DialRadius = DialSize * 10 / 24;
    const double DialStart = 135, DialSweep = 270;
    // The speaker in the middle, and the percent that replaces it on hover, both sized in step with the ring.
    const double DialSpeakerSize = 13, DialPercentSize = 10;
    // One wheel notch, as a fraction of the scale.
    const double VolumeStep = 0.02;

    static readonly Brush DialTrack = Frozen(0x24, 0xFF, 0xFF, 0xFF);
    static readonly Brush DialRest = Frozen(0x9E, 0xFF, 0xFF, 0xFF);
    static readonly Brush DialHot = Frozen(0xFF, 0x4C, 0xC2, 0xFF);
    static readonly Brush DialMuted = Frozen(0x40, 0xFF, 0xFF, 0xFF);
    static readonly Brush DialKnob = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    static readonly Brush DialKnobRim = Frozen(0xFF, 0x14, 0x15, 0x18);

    // A cross where the speaker's waves were, for muted (and for a level of zero, as Windows draws it).
    const string MuteCross = "M10.5,6 L14.5,10 M14.5,6 L10.5,10";

    IVolumeControl? volume;
    IDisposable? volumeChanges;

    // What the dial shows. Updated at once by the dial's own changes rather than waiting for Windows to
    // report them, so a fast wheel never lags behind the hand (SystemVolume leaves our own changes out of
    // its Changes stream for the same reason).
    VolumeLevel? shownVolume;
    bool volumeHovered;

    Button? volumeButton;
    Path? volumeTrack;
    Path? volumeArc;
    Ellipse? volumeKnob;
    ScaleTransform? volumeGrow;
    UIElement? volumeSpeaker;
    UIElement? volumeMuted;
    TextBlock? volumePercent;

    // Where the level comes from. Set by the composition root (SystemVolume); left unset, as every test but
    // the volume ones leaves it, the dial stays hidden and nothing reaches the machine's real volume.
    public IVolumeControl? Volume
    {
        get => volume;
        init => AttachVolume(value);
    }

    void AttachVolume(IVolumeControl? source)
    {
        volumeChanges?.Dispose();
        volume = source;
        shownVolume = source?.Current.GetValueOrDefault();
        // Windows calls back on its own threads, so a change is handed to the dispatcher; one that already
        // arrives on it (a test's fake) is applied on the spot.
        if (ClickTrace.On) ClickTrace.Write($"volume: attached, now={shownVolume?.ToString() ?? "no device"}");
        volumeChanges = source?.Changes.Subscribe(level =>
        {
            void Apply()
            {
                if (ClickTrace.On) ClickTrace.Write($"volume: changed elsewhere to {level}");
                shownVolume = level;
                PaintVolume();
            }
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(Apply);
        });
        PaintVolume();
    }

    Button VolumeDial()
    {
        volumeTrack = new Path { Stroke = DialTrack, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = DialArc(1) };
        volumeArc = new Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        volumeKnob = new Ellipse { Fill = DialKnob, Stroke = DialKnobRim, StrokeThickness = 1.5 };
        volumeSpeaker = Glyph(SpeakerWaves, SpeakerShape, size: DialSpeakerSize);
        volumeMuted = Glyph(MuteCross, SpeakerShape, size: DialSpeakerSize);
        volumePercent = new TextBlock
        {
            FontSize = DialPercentSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ClockForeground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var scale = new Canvas { Width = DialSize, Height = DialSize, Children = { volumeTrack, volumeArc, volumeKnob } };
        volumeGrow = new ScaleTransform(1, 1, DialCentre, DialCentre);
        var face = new Grid
        {
            Width = DialSize,
            Height = DialSize,
            RenderTransform = volumeGrow,
            IsHitTestVisible = false,
            Children = { scale, Centred(volumeSpeaker), Centred(volumeMuted), volumePercent },
        };

        volumeButton = new Button
        {
            Content = face,
            // The ring plus 2 DIP either side (3 before the strip shrank for the language switcher), and the
            // strip's height.
            Width = DialSize + 4,
            Height = StripButtonHeight,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetName(volumeButton, "Volume");
        // A click lists the playback devices, with mute at the end of the list (DeviceMenu). It used to mute
        // outright; Petre: "let's show sound devices on the right click of the speaker", then "let's actually
        // show it on the left click, instead of muting".
        //
        // ...but only when there is a CHOICE to make. With one playback device (or none the bar can list) the
        // menu was that one device, already ticked, above Mute: a list with nothing to pick from, one extra
        // click in the way of the only thing left to do. So then the click mutes and unmutes outright, as it
        // did before the menu. Petre: "if there's only one sound output device, mute it, don't show just one".
        // Counted at click time, like the menu is built, so a headset plugged in a minute ago brings it back.
        volumeButton.Click += (_, _) =>
        {
            MarkPressConsumed();
            ClickSpeaker();
        };
        volumeButton.MouseWheel += (_, e) =>
        {
            // Handled, so the wheel turns the dial and nothing behind it.
            e.Handled = true;
            StepVolume(e.Delta / 120.0);
        };
        volumeButton.MouseEnter += (_, _) => { volumeHovered = true; PaintVolume(); };
        volumeButton.MouseLeave += (_, _) => { volumeHovered = false; PaintVolume(); };
        Closed += (_, _) => volumeChanges?.Dispose();

        PaintVolume();
        return volumeButton;
    }

    static FrameworkElement Centred(UIElement glyph) => new Border
    {
        Child = glyph,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Up is louder, as everywhere on Windows. Snapped to the 2% grid, so a level set elsewhere to 37% moves to
    // 38 or 36 and every step after lands on the same numbers the flyout shows.
    internal void StepVolume(double notches)
    {
        if (volume is null || shownVolume is not { } now) return;
        var level = Math.Clamp(Math.Round((now.Level + notches * VolumeStep) / VolumeStep) * VolumeStep, 0, 1);
        var result = volume.SetLevel(level).Bind(() => now.Muted ? volume.SetMuted(false) : Result.Success());
        TraceVolume(result, $"step {notches:+0.##;-0.##} -> {level:P0}");
        if (result.IsFailure) return;
        shownVolume = new VolumeLevel(level, false);
        PaintVolume();
    }

    // Where the sound goes. Set by the composition root (AudioDevices); without it the click still offers mute.
    public IAudioDevices? AudioOutputs { get; init; }

    // The playback devices, the current default ticked, and choosing one makes it the default; then mute or
    // unmute, so the click that used to mute still reaches it in one more step. Built fresh each time, because
    // a headset plugged in a minute ago belongs on it.
    internal ContextMenu DeviceMenu()
    {
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);
        var outputs = AudioOutputs?.Outputs() ?? [];
        if (ClickTrace.On) ClickTrace.Write($"volume: devices [{string.Join(", ", outputs.Select(d => d.IsDefault ? $"*{d.Name}" : d.Name))}]");
        outputs.ToList().ForEach(device =>
        {
            var item = new MenuItem { Header = device.Name, IsCheckable = true, IsChecked = device.IsDefault };
            item.Click += (_, _) =>
            {
                if (device.IsDefault || AudioOutputs is null) return;
                var outcome = AudioOutputs.MakeDefault(device);
                TraceVolume(outcome, $"default device -> {device.Name}");
                Report(outcome);
            };
            menu.Items.Add(item);
        });
        if (outputs.Count > 0) menu.Items.Add(new Separator());
        var muted = shownVolume?.Muted ?? false;
        var mute = new MenuItem { Header = muted ? "Unmute" : "Mute", Icon = MenuGlyph(muted ? "♪" : "⊘") };
        mute.Click += (_, _) => ToggleMute();
        menu.Items.Add(mute);
        return menu;
    }

    // The speaker's click: the device menu when there is more than one device to choose from, otherwise mute.
    internal void ClickSpeaker()
    {
        if ((AudioOutputs?.Outputs().Count ?? 0) > 1) OpenDeviceMenu();
        else ToggleMute();
    }

    void OpenDeviceMenu()
    {
        if (volumeButton is null) return;
        var menu = DeviceMenu();
        menu.PlacementTarget = volumeButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    internal void ToggleMute()
    {
        if (volume is null || shownVolume is not { } now) return;
        var result = volume.SetMuted(!now.Muted);
        TraceVolume(result, now.Muted ? "unmute" : "mute");
        if (result.IsFailure) return;
        shownVolume = now with { Muted = !now.Muted };
        PaintVolume();
    }

    // Traced rather than shown: a failure here would be a message box per wheel notch.
    static void TraceVolume(Result result, string what)
    {
        if (ClickTrace.On) ClickTrace.Write($"volume: {what} {(result.IsSuccess ? "ok" : result.Error)}");
    }

    // Everything the dial draws comes from the level and whether the pointer is on it, so this is the one
    // place that decides it.
    void PaintVolume()
    {
        if (volumeButton is null || volumeArc is null || volumeTrack is null || volumeKnob is null || volumeGrow is null
            || volumeSpeaker is null || volumeMuted is null || volumePercent is null) return;

        // No playback device, or no volume source at all: no dial, rather than a dead one.
        volumeButton.Visibility = shownVolume is null ? Visibility.Collapsed : Visibility.Visible;
        if (shownVolume is not { } now) return;

        var silent = now.Muted || now.Level <= 0;
        var thickness = volumeHovered ? 3.2 : 2;
        volumeTrack.StrokeThickness = thickness;
        volumeArc.StrokeThickness = thickness;
        volumeArc.Data = DialArc(now.Level);
        volumeArc.Stroke = now.Muted ? DialMuted : volumeHovered ? DialHot : DialRest;

        // The knob sits on the scale at the level, and only while the pointer is on the dial.
        var knob = volumeHovered ? 7.0 : 0.0;
        var at = DialPoint(now.Level);
        volumeKnob.Width = knob;
        volumeKnob.Height = knob;
        Canvas.SetLeft(volumeKnob, at.X - knob / 2);
        Canvas.SetTop(volumeKnob, at.Y - knob / 2);

        volumeGrow.ScaleX = volumeGrow.ScaleY = volumeHovered ? 1.12 : 1;

        // The middle: the percentage while the pointer is on it, otherwise the speaker (crossed when silent).
        // Muted keeps the crossed speaker even on hover, since "muted" is the thing worth saying then.
        var showPercent = volumeHovered && !now.Muted;
        volumePercent.Text = showPercent ? $"{Math.Round(now.Level * 100)}" : "";
        volumeSpeaker.Visibility = !showPercent && !silent ? Visibility.Visible : Visibility.Collapsed;
        volumeMuted.Visibility = !showPercent && silent ? Visibility.Visible : Visibility.Collapsed;
        volumeButton.ToolTip = now.Muted ? "Muted. Click to unmute" : null;
    }

    // A point on the dial's circle, `fraction` of the way along its scale.
    internal static Point DialPoint(double fraction)
    {
        var radians = (DialStart + DialSweep * Math.Clamp(fraction, 0, 1)) * Math.PI / 180;
        return new Point(DialCentre + DialRadius * Math.Cos(radians), DialCentre + DialRadius * Math.Sin(radians));
    }

    // The scale from its start to `fraction` of the way along. Empty at zero, since an arc with no length still
    // draws its round caps as a dot.
    static Geometry DialArc(double fraction)
    {
        if (fraction <= 0) return Geometry.Empty;
        var figure = new PathFigure { StartPoint = DialPoint(0), IsClosed = false };
        figure.Segments.Add(new ArcSegment(DialPoint(fraction), new Size(DialRadius, DialRadius), 0,
            isLargeArc: DialSweep * fraction > 180, SweepDirection.Clockwise, isStroked: true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
