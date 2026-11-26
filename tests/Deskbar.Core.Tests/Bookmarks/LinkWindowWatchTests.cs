using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Bookmarks;

// Which window a link went into (LinkWindowWatch.Receiver): the first window that receives links on the bookmark's
// desktop to come to the front within the wait, else the one in front when the wait ends, else nothing.
public sealed class LinkWindowWatchTests
{
    static readonly Guid Desk = Guid.NewGuid();
    static readonly Guid OtherDesk = Guid.NewGuid();
    const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string Firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";

    sealed record Fake(int Id, string Name, Guid Desktop, string? Exe = null, string? AppId = null, string? CommandLine = null);

    readonly Subject<WindowHandle> foreground = new();
    readonly Subject<System.Reactive.Unit> deadline = new();
    readonly Dictionary<WindowHandle, Fake> windows = new()
    {
        [new WindowHandle(1)] = new(10, "Code", Desk),
        [new WindowHandle(2)] = new(20, "msedge", OtherDesk, Edge, "MSEdge"),
        [new WindowHandle(3)] = new(30, "msedge", Desk, Edge, "MSEdge"),
        [new WindowHandle(4)] = new(40, "firefox", Desk, Firefox),
        [new WindowHandle(5)] = new(50, "chrome", Desk, Chrome, "Chrome"),
        // An Edge PWA: the browser's process, the window's packaged AppUserModelID.
        [new WindowHandle(6)] = new(30, "msedge", Desk, Edge, "app.iam.ge-D9839553_en4scw7w9zwzy!App"),
    };
    readonly List<WindowHandle> desktopAsked = [];
    Maybe<WindowHandle> inFront = Maybe<WindowHandle>.None;
    Maybe<string> defaultBrowser = Maybe<string>.None;
    readonly List<string> traced = [];

    List<LinkWindow> Watch()
    {
        var noted = new List<LinkWindow>();
        LinkWindowWatch.Receiver(foreground, deadline, () => inFront,
                new LinkWindowLookups(
                    w => windows.TryGetValue(w, out var x) ? (x.Id, x.Name) : Maybe<(int, string)>.None,
                    // A window whose AppId is "throw" stands for a lookup that fails under the read.
                    w => windows[w] is { AppId: "throw" } ? throw new InvalidOperationException("the process exited")
                        : new BrowserWindowDetails(windows[w].Exe, windows[w].AppId, windows[w].CommandLine),
                    w =>
                    {
                        desktopAsked.Add(w);
                        return windows.TryGetValue(w, out var x) ? x.Desktop : Maybe<Guid>.None;
                    },
                    () => defaultBrowser),
                Desk, traced.Add)
            .Subscribe(noted.Add);
        return noted;
    }

    [Fact]
    public void The_first_browser_on_the_desktop_to_come_forward_wins()
    {
        var noted = Watch();

        foreground.OnNext(new WindowHandle(1)); // not a browser
        foreground.OnNext(new WindowHandle(2)); // a browser on another desktop
        foreground.OnNext(new WindowHandle(3));
        foreground.OnNext(new WindowHandle(5)); // too late: the first one was noted
        deadline.OnNext(default);

        Assert.Equal([new LinkWindow(new WindowHandle(3), 30)], noted);
    }

    [Fact]
    public void Firefox_counts_as_a_browser()
    {
        var noted = Watch();

        foreground.OnNext(new WindowHandle(4));

        Assert.Equal([new LinkWindow(new WindowHandle(4), 40)], noted);
    }

    // The switch restored a PWA window on that workspace: it comes forward first, and is passed over.
    [Fact]
    public void A_pwa_window_is_passed_over_for_the_plain_browser_window()
    {
        var noted = Watch();

        foreground.OnNext(new WindowHandle(6));
        foreground.OnNext(new WindowHandle(3));

        Assert.Equal([new LinkWindow(new WindowHandle(3), 30)], noted);
    }

    // The switch restored a Chrome window, but the default browser is Edge: the link went to Edge.
    [Fact]
    public void A_browser_that_is_not_the_default_is_passed_over()
    {
        defaultBrowser = Edge;
        var noted = Watch();

        foreground.OnNext(new WindowHandle(5));
        foreground.OnNext(new WindowHandle(3));

        Assert.Equal([new LinkWindow(new WindowHandle(3), 30)], noted);
    }

    [Fact]
    public void The_default_browsers_plain_window_in_front_at_the_deadline_is_taken()
    {
        defaultBrowser = Chrome;
        var noted = Watch();
        inFront = new WindowHandle(5);

        deadline.OnNext(default);

        Assert.Equal([new LinkWindow(new WindowHandle(5), 50)], noted);
    }

    // The browser on that desktop was already in front, so nothing came forward: the window in front at the deadline.
    [Fact]
    public void With_no_browser_coming_forward_the_one_in_front_at_the_deadline_is_taken()
    {
        var noted = Watch();
        inFront = new WindowHandle(5);

        foreground.OnNext(new WindowHandle(1));
        deadline.OnNext(default);

        Assert.Equal([new LinkWindow(new WindowHandle(5), 50)], noted);
    }

    [Theory]
    [InlineData(1)] // not a browser
    [InlineData(2)] // a browser on another desktop
    [InlineData(6)] // a PWA
    [InlineData(9)] // a window that is already gone
    public void Nothing_is_noted_when_the_window_in_front_at_the_deadline_does_not_qualify(int handle)
    {
        var noted = Watch();
        inFront = new WindowHandle(handle);

        deadline.OnNext(default);

        Assert.Empty(noted);
    }

    [Fact]
    public void Nothing_is_noted_after_the_deadline()
    {
        var noted = Watch();

        deadline.OnNext(default);
        foreground.OnNext(new WindowHandle(3));

        Assert.Empty(noted);
    }

    // The desktop is a cross-process COM call: asked only of a window that passed everything else.
    [Fact]
    public void Only_a_window_that_receives_links_is_asked_its_desktop()
    {
        Watch();

        foreground.OnNext(new WindowHandle(1));
        foreground.OnNext(new WindowHandle(6));
        foreground.OnNext(new WindowHandle(2));

        Assert.Equal([new WindowHandle(2)], desktopAsked);
    }

    // A lookup that throws costs that one window, traced, and the watch carries on to the window the link went into.
    [Fact]
    public void A_lookup_that_throws_passes_that_window_over_and_keeps_watching()
    {
        windows[new WindowHandle(8)] = new(80, "msedge", Desk, Edge, "throw");
        var noted = Watch();

        foreground.OnNext(new WindowHandle(8));
        foreground.OnNext(new WindowHandle(3));

        Assert.Equal([new LinkWindow(new WindowHandle(3), 30)], noted);
        Assert.Single(traced);
    }

    [Theory]
    [InlineData("msedge", true)]
    [InlineData("chrome", true)]
    [InlineData("Brave", true)]
    [InlineData("firefox", true)]
    [InlineData("Code", false)]
    [InlineData("explorer", false)]
    public void Which_processes_are_browsers(string process, bool browser) =>
        Assert.Equal(browser, LinkWindowWatch.IsBrowser(process));

    [Theory]
    [InlineData("MSEdge", null, false)]
    [InlineData("Chrome.mcpchromeeeeb218.Default", null, false)]
    [InlineData("outlook.cloud.microsoft-6A7975FB_3y1e9vxbwqkga!App", null, true)]
    [InlineData("Chrome._crx_abcdefghijklmnop", null, true)]
    // The window answered, so a shared process's --app-id (another PWA's) does not decide.
    [InlineData("MSEdge", "msedge.exe --app-id=abc", false)]
    // The window did not answer: the command line decides.
    [InlineData(null, "msedge.exe --app-id=abc", true)]
    [InlineData(null, "msedge.exe --profile-directory=Default", false)]
    [InlineData(null, null, false)]
    public void Which_windows_are_app_windows(string? appId, string? commandLine, bool app) =>
        Assert.Equal(app, LinkWindowWatch.IsAppWindow(new BrowserWindowDetails(Edge, appId, commandLine)));

    // With the window's exe unreadable, the default browser is compared by file name.
    [Fact]
    public void A_browser_whose_exe_is_hidden_is_compared_by_name()
    {
        Assert.True(LinkWindowWatch.Receives("msedge", new BrowserWindowDetails(null, "MSEdge", null), Edge));
        Assert.False(LinkWindowWatch.Receives("chrome", new BrowserWindowDetails(null, "Chrome", null), Edge));
    }
}
