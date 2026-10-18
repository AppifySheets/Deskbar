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

    // Nothing narrows it: every window of the browser, since showing the browser beats showing nothing.
    [Fact]
    public void With_nothing_to_narrow_it_every_browser_window_carries_it() =>
        Assert.Equal([EdgeWork.Handle, EdgeHome.Handle, YouTubeMusic.Handle],
            FloatingBar.Sounding(All, new HashSet<int> { 100 }, Tabs()).OrderBy(h => h.Value));

    [Fact]
    public void Silence_marks_nothing() =>
        Assert.Empty(FloatingBar.Sounding(All, new HashSet<int>(), Tabs()));

    // Chromium names a playing tab "<title> - Audio playing"; a muted one "... - Audio muted", which is silent.
    [Theory]
    [InlineData("Lofi beats - YouTube - Audio playing", true)]
    [InlineData("Lofi beats - YouTube - Playing audio", true)]
    [InlineData("Lofi beats - YouTube - Audio playing - Audio muted", false)]
    [InlineData("Pull requests · AppifySheets/TaskSpaces", false)]
    public void A_tab_name_says_whether_it_is_playing(string name, bool playing) =>
        Assert.Equal(playing, BrowserTabs.PlaysAudio(name));
}
