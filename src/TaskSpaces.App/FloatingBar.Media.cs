using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using TaskSpaces.Windows.Audio;

namespace TaskSpaces.App;

// THE NOW-PLAYING LINE above the pins. Petre: "let's add audio controls, pause, next, previous", then "a" from
// three mockups: the player's icon, the track's title, its artist and the app, and previous, play or pause, and
// next. Shown only while Windows has a media session; gone when nothing does.
//
// The session, its track and its controls come from IMediaControl (MediaControl in production, Windows' own media
// sessions, the ones the keyboard's media keys drive). The player's name and icon come from its AppUserModelID,
// through Start's list of apps (ITaskbarPins.NameOfApp / ImageOfApp).
public partial class FloatingBar
{
    // Filled, no outline: the transport glyphs read as solid shapes, like every player's.
    const string PreviousBar = "M4,3 L4,13";
    const string PreviousArrow = "M13,3.5 L13,12.5 L5.8,8 Z";
    const string NextBar = "M12,3 L12,13";
    const string NextArrow = "M3,3.5 L3,12.5 L10.2,8 Z";
    const string PauseShape = "M3.5,3 L6.5,3 L6.5,13 L3.5,13 Z M9.5,3 L12.5,3 L12.5,13 L9.5,13 Z";
    const string PlayShape = "M4.5,2.8 L4.5,13.2 L13,8 Z";

    static readonly Brush NowPlayingTitleInk = Frozen(0xF0, 0xFF, 0xFF, 0xFF);
    static readonly Brush NowPlayingDetailInk = Frozen(0x8C, 0xFF, 0xFF, 0xFF);

    IMediaControl? media;
    readonly Dictionary<string, (string? Name, ImageSource? Icon)> players = [];

    Image? playerIcon;
    TextBlock? trackTitle;
    TextBlock? trackDetail;
    Button? previousButton;
    Button? playPauseButton;
    Button? nextButton;

    public IMediaControl? Media
    {
        get => media;
        init
        {
            media = value;
            var subscription = value?.Changed.Subscribe(_ => Dispatcher.BeginInvoke(PaintNowPlaying));
            Closed += (_, _) => subscription?.Dispose();
            PaintNowPlaying();
        }
    }

    void BuildNowPlaying()
    {
        playerIcon = new Image { Width = 16, Height = 16, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(playerIcon, BitmapScalingMode.HighQuality);
        trackTitle = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = NowPlayingTitleInk, TextTrimming = TextTrimming.CharacterEllipsis };
        trackDetail = new TextBlock { FontSize = 9.5, Foreground = NowPlayingDetailInk, TextTrimming = TextTrimming.CharacterEllipsis };

        previousButton = TransportButton("Previous", Glyph(PreviousBar, PreviousArrow, size: 12), () => media?.Previous());
        playPauseButton = TransportButton("Pause", Glyph(PlayShape, PlayShape, size: 12), () => media?.PlayPause());
        nextButton = TransportButton("Next", Glyph(NextBar, NextArrow, size: 12), () => media?.Next());

        // From the right edge in: next, play or pause, previous. Then the icon and the text take what is left,
        // and clicking either jumps to the player.
        new[] { nextButton, playPauseButton, previousButton }.ToList().ForEach(b =>
        {
            DockPanel.SetDock(b, Dock.Right);
            NowPlayingLine.Children.Add(b);
        });
        var player = new Button
        {
            Content = new DockPanel
            {
                Children =
                {
                    new Border { Child = playerIcon, Margin = new Thickness(2, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Children = { trackTitle, trackDetail }, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = BareStretchButton,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(player, "Now playing");
        player.Click += (_, _) =>
        {
            MarkPressConsumed();
            ShowPlayer();
        };
        NowPlayingLine.Children.Add(player);
    }

    Button TransportButton(string name, UIElement glyph, Action press)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 22,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            ToolTip = name,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            press();
        };
        return button;
    }

    // Everything the line shows comes from the current session, so this is the one place that decides it.
    void PaintNowPlaying()
    {
        if (trackTitle is null || trackDetail is null || playerIcon is null || playPauseButton is null || previousButton is null || nextButton is null) return;
        if (media?.Current is not { HasValue: true } current)
        {
            NowPlayingLine.Visibility = Visibility.Collapsed;
            return;
        }

        var now = current.Value;
        var (name, icon) = Player(now.AppId);
        trackTitle.Text = now.Title.Length > 0 ? now.Title : name ?? "Playing";
        trackDetail.Text = string.Join(" · ", new[] { now.Artist, name ?? "" }.Where(s => s.Length > 0));
        trackDetail.Visibility = trackDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        playerIcon.Source = icon;

        // Pause while playing, play while paused: the button says what a press will DO.
        playPauseButton.Content = now.Playing ? Glyph(PauseShape, PauseShape, size: 12) : Glyph(PlayShape, PlayShape, size: 12);
        var label = now.Playing ? "Pause" : "Play";
        playPauseButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(playPauseButton, label);

        // A control the player does not offer stays where it is, faded and inert, so the other two do not jump.
        Offer(previousButton, now.CanPrevious);
        Offer(playPauseButton, now.CanPlayPause);
        Offer(nextButton, now.CanNext);

        NowPlayingLine.Visibility = Visibility.Visible;
    }

    static void Offer(Button button, bool offered)
    {
        button.IsEnabled = offered;
        button.Opacity = offered ? 1 : 0.35;
    }

    // A player's name and icon, looked up once per app. A miss is not cached, since Start's list can be slow to
    // answer for an app that has only just been installed.
    (string? Name, ImageSource? Icon) Player(string appId)
    {
        if (players.TryGetValue(appId, out var known) && known.Icon is not null) return known;
        var name = pins?.NameOfApp(appId);
        ImageSource? icon = null;
        pins?.ImageOfApp(appId, 32).Map(ToImageSource).Tap(found => icon = found);
        players[appId] = (name, icon);
        return (name, icon);
    }

    // Clicking the line brings the player forward: the first window whose AppUserModelID is the session's.
    void ShowPlayer()
    {
        if (media?.Current is not { HasValue: true } current || pins is null) return;
        var window = manager.KnownWindows.FirstOrDefault(w => string.Equals(pins.AppIdOf(w.Handle), current.Value.AppId, StringComparison.OrdinalIgnoreCase));
        if (ClickTrace.On) ClickTrace.Write($"media: show player {current.Value.AppId} window={(window is null ? "none" : $"{window.Handle.Value:X}")}");
        if (window is not null) Report(activator.Activate(window.Handle));
    }
}
