using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Windows.Audio;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// The volume dial in the taskbar strip (Petre: "add volume as a scrollable wheel", then "c": a ring round the
// speaker). Every bar here gets a FakeVolume: the real SystemVolume would change the volume of whoever runs
// the suite.
public class FloatingBarVolumeTests(ITestOutputHelper output)
{
    sealed class FakeVolume(double level, bool muted = false) : IVolumeControl
    {
        public readonly Subject<VolumeLevel> Elsewhere = new();
        public VolumeLevel Now { get; private set; } = new(level, muted);
        public Maybe<VolumeLevel> Current => Now;
        public IObservable<VolumeLevel> Changes => Elsewhere;

        public Result SetLevel(double value)
        {
            Now = Now with { Level = value };
            return Result.Success();
        }

        public Result SetMuted(bool value)
        {
            Now = Now with { Muted = value };
            return Result.Success();
        }
    }

    static FloatingBar Bar(IVolumeControl? volume)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, Volume = volume };
        bar.ShowBar();
        bar.UpdateLayout();
        return bar;
    }

    static Button Dial(FloatingBar bar) =>
        ((Panel)bar.FindName("ShellStrip")!).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Volume");

    static void Wheel(Button dial, int delta) =>
        dial.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.MouseWheelEvent });

    static void Hover(Button dial, bool on) =>
        dial.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = on ? UIElement.MouseEnterEvent : UIElement.MouseLeaveEvent });

    static string Percent(Button dial) => ((Grid)dial.Content).Children.OfType<TextBlock>().Single().Text;

    // No source, or a machine with no playback device: no dial, rather than one that does nothing.
    [Fact]
    public void Without_a_volume_source_there_is_no_dial() => StaThread.Run(() =>
    {
        var bar = Bar(null);
        Assert.Equal(Visibility.Collapsed, Dial(bar).Visibility);
        bar.Close();
    });

    [Fact]
    public void With_a_volume_source_the_dial_shows() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeVolume(0.4));
        Assert.Equal(Visibility.Visible, Dial(bar).Visibility);
        bar.Close();
    });

    // 2% a notch, up is louder, and a fast wheel's several notches in one event move by that many.
    [Fact]
    public void The_wheel_moves_the_volume_two_percent_a_notch() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.4);
        var bar = Bar(volume);
        var dial = Dial(bar);

        Wheel(dial, 120);
        Assert.Equal(0.42, volume.Now.Level, 6);
        Wheel(dial, -360);
        Assert.Equal(0.36, volume.Now.Level, 6);

        bar.Close();
    });

    // A level set elsewhere off the 2% grid joins it on the first notch, so the numbers the dial shows after
    // that are the ones Windows' flyout shows.
    [Fact]
    public void A_level_off_the_grid_snaps_onto_it() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.37);
        var bar = Bar(volume);
        Wheel(Dial(bar), 120);
        Assert.Equal(0.40, volume.Now.Level, 6);
        bar.Close();
    });

    [Fact]
    public void The_wheel_stops_at_silence_and_at_full() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.02);
        var bar = Bar(volume);
        var dial = Dial(bar);

        Wheel(dial, -600);
        Assert.Equal(0, volume.Now.Level, 6);
        Wheel(dial, 120 * 80);
        Assert.Equal(1, volume.Now.Level, 6);

        bar.Close();
    });

    // As with the keyboard's volume keys: turning it up while muted unmutes.
    [Fact]
    public void The_wheel_unmutes() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.4, muted: true);
        var bar = Bar(volume);
        Wheel(Dial(bar), 120);
        Assert.False(volume.Now.Muted);
        bar.Close();
    });

    sealed class FakeDevices : IAudioDevices
    {
        public List<AudioDevice> Devices { get; } =
            [new("speakers", "Speakers", IsDefault: true), new("headset", "Headset", IsDefault: false)];
        public List<string> MadeDefault { get; } = [];
        public IReadOnlyList<AudioDevice> Outputs() => Devices;
        public Result MakeDefault(AudioDevice device)
        {
            MadeDefault.Add(device.Id);
            return Result.Success();
        }
    }

    // Petre: "let's actually show it on the left click, instead of muting". The click's menu lists the playback
    // devices with the default ticked, then mute.
    [Fact]
    public void The_click_menu_lists_the_devices_then_mute() => StaThread.Run(() =>
    {
        var devices = new FakeDevices();
        var bar = BarWith(new FakeVolume(0.4), devices);

        var items = bar.DeviceMenu().Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Speakers", "Headset", "Mute"], items.Select(i => (string)i.Header));
        Assert.True(items[0].IsChecked);
        Assert.False(items[1].IsChecked);
        bar.Close();
    });

    [Fact]
    public void Choosing_a_device_makes_it_the_default() => StaThread.Run(() =>
    {
        var devices = new FakeDevices();
        var bar = BarWith(new FakeVolume(0.4), devices);

        bar.DeviceMenu().Items.OfType<MenuItem>().Single(i => (string)i.Header == "Headset")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(["headset"], devices.MadeDefault);
        bar.Close();
    });

    // Mute is still one click into the menu, and says which way it goes.
    [Fact]
    public void Mute_from_the_menu_mutes_and_then_offers_unmute() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.4);
        var bar = BarWith(volume, new FakeDevices());

        bar.DeviceMenu().Items.OfType<MenuItem>().Single(i => (string)i.Header == "Mute")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(volume.Now.Muted);
        Assert.Contains(bar.DeviceMenu().Items.OfType<MenuItem>(), i => (string)i.Header == "Unmute");
        bar.Close();
    });

    static FloatingBar BarWith(IVolumeControl volume, IAudioDevices devices)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, Volume = volume, AudioOutputs = devices };
        bar.ShowBar();
        bar.UpdateLayout();
        return bar;
    }

    // The percentage shows only while the pointer is on the dial; at rest the middle is the speaker.
    [Fact]
    public void Hovering_shows_the_percentage() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeVolume(0.64));
        var dial = Dial(bar);

        Assert.Equal("", Percent(dial));
        Hover(dial, on: true);
        Assert.Equal("64", Percent(dial));
        Hover(dial, on: false);
        Assert.Equal("", Percent(dial));

        bar.Close();
    });

    // A change made elsewhere (the volume keys, Windows' flyout) reaches the dial, and the next notch starts
    // from it rather than from what the dial last set.
    [Fact]
    public void A_change_made_elsewhere_moves_the_dial() => StaThread.Run(() =>
    {
        var volume = new FakeVolume(0.4);
        var bar = Bar(volume);
        var dial = Dial(bar);

        volume.Elsewhere.OnNext(new VolumeLevel(0.8, false));
        Hover(dial, on: true);
        Assert.Equal("80", Percent(dial));

        Wheel(dial, 120);
        Assert.Equal(0.82, volume.Now.Level, 6);

        bar.Close();
    });

    // The scale runs 270 degrees from bottom left, over the top, to bottom right, so silence and full sit at
    // the same height either side of the bottom and half volume is straight up.
    [Fact]
    public void The_scale_runs_from_bottom_left_over_the_top_to_bottom_right()
    {
        var silence = FloatingBar.DialPoint(0);
        var half = FloatingBar.DialPoint(0.5);
        var full = FloatingBar.DialPoint(1);
        output.WriteLine($"silence={silence} half={half} full={full}");

        Assert.True(silence.X < 12 && silence.Y > 12);
        Assert.Equal(12, half.X, 6);
        Assert.Equal(2, half.Y, 6);
        Assert.True(full.X > 12);
        Assert.Equal(silence.Y, full.Y, 6);
    }
}
