using System.Reactive;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Windows.Audio;

namespace Deskbar.Windows.Tests;

// The now-playing line (Petre: "let's add audio controls, pause, next, previous", then "a"). Every bar here gets
// a FakeMedia: the real MediaControl would pause or skip whatever is playing on the machine running the suite.
public class FloatingBarMediaTests
{
    sealed class FakeMedia : IMediaControl
    {
        public readonly Subject<Unit> Updates = new();
        public NowPlaying? Now;
        public List<string> Pressed { get; } = [];
        public Maybe<NowPlaying> Current => Now is { } now ? now : Maybe<NowPlaying>.None;
        public IObservable<Unit> Changed => Updates;
        public void PlayPause() => Pressed.Add("playpause");
        public void Next() => Pressed.Add("next");
        public void Previous() => Pressed.Add("previous");
    }

    static readonly NowPlaying Song = new("music.youtube.com!App", "Shma Israel", "Alilo", Playing: true, CanPrevious: true, CanPlayPause: true, CanNext: true);

    static FloatingBar Bar(IMediaControl? media)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, Media = media };
        bar.ShowBar();
        bar.UpdateLayout();
        return bar;
    }

    static DockPanel Line(FloatingBar bar) => (DockPanel)bar.FindName("NowPlayingLine")!;

    static Button Named(FloatingBar bar, string name) =>
        Line(bar).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    static IEnumerable<string> Texts(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .SelectMany(child => (child is TextBlock t ? [t.Text] : Array.Empty<string>()).Concat(Texts(child)));

    [Fact]
    public void Without_a_media_source_there_is_no_line() => StaThread.Run(() =>
    {
        var bar = Bar(null);
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
        bar.Close();
    });

    // Nothing has a media session: no line, rather than an empty one.
    [Fact]
    public void With_nothing_playing_there_is_no_line() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeMedia());
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
        bar.Close();
    });

    [Fact]
    public void A_playing_track_shows_its_title_and_artist() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeMedia { Now = Song });

        Assert.Equal(Visibility.Visible, Line(bar).Visibility);
        var texts = Texts(Line(bar)).ToList();
        Assert.Contains("Shma Israel", texts);
        Assert.Contains("Alilo", texts);
        bar.Close();
    });

    // The two lines fit the line they sit on, at the bar's text size. Petre's screenshot at the +4 default: the
    // title rode up over the line's top and the artist (Georgian, whose letters reach further below the line)
    // was cut off by the icons under it, because the player button was a fixed 30 DIP and the two lines needed
    // about 39. Measured on the bar: a 40 px button holding 58 px of text.
    [Fact]
    public void The_title_and_artist_fit_inside_the_line() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeMedia { Now = Song with { Artist = "თემურ თათარაშვილი" } });
        bar.UpdateLayout();

        var player = Named(bar, "Now playing");
        var text = Descendants(player).OfType<StackPanel>().First();
        // Measured UNCONSTRAINED: WPF clamps a child's DesiredSize to the space it was offered, so read inside a
        // 30 DIP button it says 30 and the overflow is invisible. That is how a first version of this test
        // passed against the bug.
        var tall = player.ActualHeight;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(tall >= text.DesiredSize.Height,
            $"the button is {tall:F1} DIP tall and its two lines need {text.DesiredSize.Height:F1}");
        bar.Close();
    });

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // The middle button says what a press will do: pause while playing, play while paused.
    [Fact]
    public void The_middle_button_pauses_a_playing_track_and_plays_a_paused_one() => StaThread.Run(() =>
    {
        var media = new FakeMedia { Now = Song };
        var bar = Bar(media);
        Assert.Single(Line(bar).Children.OfType<Button>(), b => AutomationProperties.GetName(b) == "Pause");

        media.Now = Song with { Playing = false };
        media.Updates.OnNext(Unit.Default);
        bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.Single(Line(bar).Children.OfType<Button>(), b => AutomationProperties.GetName(b) == "Play");
        bar.Close();
    });

    [Fact]
    public void The_buttons_drive_the_session() => StaThread.Run(() =>
    {
        var media = new FakeMedia { Now = Song };
        var bar = Bar(media);

        Named(bar, "Previous").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Named(bar, "Pause").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Named(bar, "Next").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(["previous", "playpause", "next"], media.Pressed);
        bar.Close();
    });

    // A control the player does not offer stays in place but does nothing.
    [Fact]
    public void A_control_the_player_does_not_offer_is_disabled() => StaThread.Run(() =>
    {
        var bar = Bar(new FakeMedia { Now = Song with { CanNext = false } });
        Assert.False(Named(bar, "Next").IsEnabled);
        Assert.True(Named(bar, "Previous").IsEnabled);
        bar.Close();
    });
}
