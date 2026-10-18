using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Windows.Audio;

namespace TaskSpaces.App;

// TWO MARKS ON WINDOW ICONS, both "c" from three mockups.
//
//   tabs    Petre: "let's include tab counts in browsers in their icons". A browser window with two or more tabs
//           has the count small at its top. Counts come from BrowserTabs. The mockup also stacked two card
//           outlines behind the icon; on the bar they were "tab counts are good; but i don't want those rings",
//           "just not browser square rings", so the count stands alone.
//   sound   Petre: "if any window is playing sound, show it on it so i can quickly see what's the source of
//           sound". A soft green ring pulses round the icon of every window making sound, and round its pin.
//
// Which window is sounding is decided here (see Sounding), from AudioActivity's processes and BrowserTabs'
// readings, once per rebuild and before any icon is built.
public partial class FloatingBar
{
    static readonly Brush TabCountInk = Frozen(0xE6, 0xFF, 0xFF, 0xFF);
    static readonly Brush SoundRing = Frozen(0xFF, 0x6E, 0xE7, 0xA6);

    IBrowserTabs? tabs;
    IAudioActivity? sound;
    HashSet<WindowHandle> sounding = [];

    // Where the readings come from. Set by the composition root; left unset, as the tests but these leave them,
    // no window has a count or a ring.
    public IBrowserTabs? Tabs
    {
        get => tabs;
        init
        {
            tabs = value;
            var subscription = value?.Changed.Subscribe(_ => Dispatcher.BeginInvoke(Rebuild));
            Closed += (_, _) => subscription?.Dispose();
        }
    }

    public IAudioActivity? Sound
    {
        get => sound;
        init
        {
            sound = value;
            var subscription = value?.Changed.Subscribe(_ => Dispatcher.BeginInvoke(Rebuild));
            Closed += (_, _) => subscription?.Dispose();
        }
    }

    // Every rebuild, before its icons: tell BrowserTabs which windows are on show, and work out which of them
    // are sounding.
    void NoteActivity(Overview overview)
    {
        var windows = overview.Pinned
            .Concat(overview.Workspaces.SelectMany(w => w.Running))
            .Concat(overview.OtherDesktops.SelectMany(d => d.Windows))
            .Select(r => r.Window)
            .DistinctBy(w => w.Handle)
            .ToList();
        tabs?.Watch(windows);
        var before = sounding;
        sounding = Sounding(windows, sound?.Playing ?? new HashSet<int>(), h => tabs?.Of(h));
        if (ClickTrace.On && !sounding.SetEquals(before))
            ClickTrace.Write($"sound: playing pids=[{string.Join(",", sound?.Playing ?? new HashSet<int>())}] " +
                             $"windows=[{string.Join(", ", windows.Where(w => sounding.Contains(w.Handle)).Select(w => $"{w.Handle.Value:X}/{w.ProcessName}"))}]");
    }

    // Which windows the sound is coming from. An ordinary app: every window of a playing process. A browser
    // plays all its sound through one helper, so its windows are narrowed down:
    //
    //   1. the windows with a tab marked as playing audio, when there are any;
    //   2. otherwise its windows with no tab strip, the installed web apps (YouTube Music today), when there are any;
    //   3. otherwise every window of it, because showing the browser beats showing nothing.
    internal static HashSet<WindowHandle> Sounding(IReadOnlyList<WindowInfo> windows, IReadOnlySet<int> playing, Func<WindowHandle, TabInfo?> tabsOf) =>
        windows.Where(w => playing.Contains(w.ProcessId))
            .GroupBy(w => w.ProcessId)
            .SelectMany(process =>
            {
                if (!BrowserTabs.IsBrowser(process.First())) return process.AsEnumerable();
                var marked = process.Where(w => tabsOf(w.Handle)?.PlayingAudio == true).ToList();
                if (marked.Count > 0) return marked;
                var apps = process.Where(w => tabsOf(w.Handle) is { Count: 0 }).ToList();
                return apps.Count > 0 ? apps : process.AsEnumerable();
            })
            .Select(w => w.Handle)
            .ToHashSet();

    // The two marks around an icon that WithState has already dressed: the count and the ring on top, neither
    // hit-testable, both overlaying the 24 DIP cell so a row measures as before.
    UIElement WithActivity(UIElement icon, WindowRow row)
    {
        var count = tabs?.Of(row.Window.Handle)?.Count ?? 0;
        var loud = sounding.Contains(row.Window.Handle);
        if (count < 2 && !loud) return icon;

        var cell = new Grid();
        cell.Children.Add(icon);
        if (count >= 2)
            cell.Children.Add(new TextBlock
            {
                Text = count > 99 ? "99+" : count.ToString(),
                FontSize = 7.5,
                FontWeight = FontWeights.Bold,
                Foreground = TabCountInk,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -3, 0),
                IsHitTestVisible = false,
            });
        if (loud) cell.Children.Add(Ring(24));
        return cell;
    }

    // The sound ring: a circle round the icon whose opacity breathes. Still, at full strength, when Windows'
    // own animations are turned off, which is the setting a person who finds motion distracting has chosen.
    static Ellipse Ring(double size)
    {
        var ring = new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = SoundRing,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (SystemParameters.ClientAreaAnimation)
            ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.95, 0.35, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        return ring;
    }
}
