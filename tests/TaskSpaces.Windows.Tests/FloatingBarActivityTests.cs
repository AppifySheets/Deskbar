using System.Reactive;
using System.Reactive.Subjects;
using TaskSpaces.App;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Tests;

// Which windows the sound ring goes on (Petre: "if any window is playing sound, show it on it so i can quickly
// see what's the source of sound"). Measured: a browser plays all its sound through one helper process whose
// parent owns every one of its windows, so the session alone cannot say which window it was. These pin the
// rules that narrow it down.
public class FloatingBarActivityTests
{
    static WindowInfo Window(int handle, int pid, string process) =>
        new(new WindowHandle(handle), pid, process, $@"C:\Apps\{process}.exe", $"window {handle}", null);

    static readonly WindowInfo EdgeWork = Window(1, 100, "msedge");
    static readonly WindowInfo EdgeHome = Window(2, 100, "msedge");
    static readonly WindowInfo YouTubeMusic = Window(3, 100, "msedge"); // an installed web app, hosted by Edge
    static readonly WindowInfo Spotify = Window(4, 200, "Spotify");
    static readonly WindowInfo SpotifyMini = Window(5, 200, "Spotify");
    static readonly WindowInfo Code = Window(6, 300, "Code");

    static readonly IReadOnlyList<WindowInfo> All = [EdgeWork, EdgeHome, YouTubeMusic, Spotify, SpotifyMini, Code];

    static Func<WindowHandle, TabInfo?> Tabs(params (WindowInfo Window, TabInfo Info)[] readings) =>
        handle => readings.FirstOrDefault(r => r.Window.Handle == handle).Info;

    // An ordinary app: every window of the process that is playing.
    [Fact]
    public void An_apps_windows_all_carry_its_sound() =>
        Assert.Equal([Spotify.Handle, SpotifyMini.Handle],
            FloatingBar.Sounding(All, new HashSet<int> { 200 }, Tabs()).OrderBy(h => h.Value));

    // A browser with a tab marked as playing: only that tab's window.
    [Fact]
    public void A_browsers_sound_goes_to_the_window_with_the_playing_tab() =>
        Assert.Equal([EdgeHome.Handle],
            FloatingBar.Sounding(All, new HashSet<int> { 100 },
                Tabs((EdgeWork, new TabInfo(5, false)), (EdgeHome, new TabInfo(2, true)), (YouTubeMusic, new TabInfo(0, false)))));

    // No tab marked, and the browser hosts an installed web app (no tab strip): the app. That is today's case
    // on Petre's machine, YouTube Music playing in Edge's process.
    [Fact]
    public void With_no_playing_tab_the_sound_goes_to_the_installed_web_app() =>
        Assert.Equal([YouTubeMusic.Handle],
            FloatingBar.Sounding(All, new HashSet<int> { 100 },
                Tabs((EdgeWork, new TabInfo(5, false)), (EdgeHome, new TabInfo(2, false)), (YouTubeMusic, new TabInfo(0, false)))));

    // Two installed web apps in one browser process, YouTube Music playing and Outlook not (Petre: "yt music is
    // playing and outlook is also ringed", "both pwa"). Windows' media session names the playing app by the same
    // AppUserModelID the YouTube Music window carries, so only that one is ringed.
    [Fact]
    public void Of_two_web_apps_the_one_the_media_session_names_carries_the_sound()
    {
        var outlook = Window(7, 100, "msedge");
        var ids = new Dictionary<WindowHandle, string>
        {
            [YouTubeMusic.Handle] = "music.youtube.com-5929F88E_vezhnr0wkvrcy!App",
            [outlook.Handle] = "outlook.cloud.microsoft-6A7975FB_3y1e9vxbwqkga!App",
        };
        var tabs = Tabs((EdgeWork, new TabInfo(5, false)), (YouTubeMusic, new TabInfo(0, false)), (outlook, new TabInfo(0, false)));
        string? AppIdOf(WindowInfo w) => ids.GetValueOrDefault(w.Handle);

        Assert.Equal([YouTubeMusic.Handle],
            FloatingBar.Sounding([.. All, outlook], new HashSet<int> { 100 }, tabs, AppIdOf, "music.youtube.com-5929F88E_vezhnr0wkvrcy!App"));
        // A session for an app with no window here narrows nothing: both web apps, as before.
        Assert.Equal([YouTubeMusic.Handle, outlook.Handle],
            FloatingBar.Sounding([.. All, outlook], new HashSet<int> { 100 }, tabs, AppIdOf, "Spotify.exe").OrderBy(h => h.Value));
    }

    // Nothing narrows it: every window of the browser, since showing the browser beats showing nothing.
    [Fact]
    public void With_nothing_to_narrow_it_every_browser_window_carries_it() =>
        Assert.Equal([EdgeWork.Handle, EdgeHome.Handle, YouTubeMusic.Handle],
            FloatingBar.Sounding(All, new HashSet<int> { 100 }, Tabs()).OrderBy(h => h.Value));

    [Fact]
    public void Silence_marks_nothing() =>
        Assert.Empty(FloatingBar.Sounding(All, new HashSet<int>(), Tabs()));

    // The aging curve (Petre: "a", fade to grey): full colour for 15 minutes, fully aged at 8 hours, and most of
    // the change in the first hours.
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(15, 0.0)]
    [InlineData(480, 1.0)]
    [InlineData(1440, 1.0)]
    public void Icons_age_from_fresh_to_fully_grey(int minutesUnused, double age) =>
        Assert.Equal(age, FloatingBar.AgeOf(TimeSpan.FromMinutes(minutesUnused)), 6);

    [Fact]
    public void Aging_is_front_loaded()
    {
        var oneHour = FloatingBar.AgeOf(TimeSpan.FromHours(1));
        var fourHours = FloatingBar.AgeOf(TimeSpan.FromHours(4));
        Assert.InRange(oneHour, 0.1, 0.3);
        Assert.True(fourHours > oneHour * 2, $"1 h {oneHour:F2}, 4 h {fourHours:F2}");
        Assert.True(fourHours < 1);
    }

    // VS Code's editor tabs against the activity bar's view switchers, by the classes measured on Petre's windows.
    [Theory]
    [InlineData("tab tab-actions-right sizing-fit has-icon tab-border-bottom connected-tab-top-row active selected tab-border-top", true)]
    [InlineData("tab tab-actions-right sizing-fit has-icon connected-tab-top-row", true)]
    [InlineData("action-item icon checked", false)]
    [InlineData("action-item icon", false)]
    [InlineData("", false)]
    public void A_vscode_editor_tab_is_told_by_its_class(string className, bool editorTab) =>
        Assert.Equal(editorTab, BrowserTabs.IsEditorTab(className));

    // Chromium names a playing tab "<title> - Audio playing"; a muted one "... - Audio muted", which is silent.
    [Theory]
    [InlineData("Lofi beats - YouTube - Audio playing", true)]
    [InlineData("Lofi beats - YouTube - Playing audio", true)]
    [InlineData("Lofi beats - YouTube - Audio playing - Audio muted", false)]
    [InlineData("Pull requests · AppifySheets/TaskSpaces", false)]
    public void A_tab_name_says_whether_it_is_playing(string name, bool playing) =>
        Assert.Equal(playing, BrowserTabs.PlaysAudio(name));
}
