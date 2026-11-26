using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Bookmarks;

// The opened-links tracker (option B of the link chip question): which browser window each link went into, kept in
// memory only, and the rule that picks that window from the foreground changes after an open.
public sealed class OpenedLinksTests
{
    static readonly Guid First = Guid.NewGuid();
    static readonly Guid Second = Guid.NewGuid();
    static readonly LinkWindow Edge = new(new WindowHandle(0x100), 4000);
    static readonly LinkWindow Other = new(new WindowHandle(0x200), 5000);

    static bool Alive(LinkWindow _) => true;

    [Fact]
    public void A_noted_link_is_open_while_its_window_lives()
    {
        var links = new OpenedLinks();

        links.Note(First, Edge);

        Assert.True(links.IsOpen(First, Alive));
        Assert.False(links.IsOpen(First, _ => false));
        Assert.False(links.IsOpen(Second, Alive));
    }

    [Fact]
    public void A_window_that_goes_takes_every_link_noted_on_it()
    {
        var links = new OpenedLinks();
        var third = Guid.NewGuid();
        links.Note(First, Edge);
        links.Note(Second, Edge);
        links.Note(third, Other);

        links.Forget(Edge.Handle);

        Assert.False(links.IsOpen(First, Alive));
        Assert.False(links.IsOpen(Second, Alive));
        Assert.True(links.IsOpen(third, Alive));
    }

    [Fact]
    public void A_removed_bookmark_is_forgotten()
    {
        var links = new OpenedLinks();
        links.Note(First, Edge);

        links.Forget(First);

        Assert.False(links.IsOpen(First, Alive));
        Assert.Empty(links.Noted);
    }

    // The bar redraws on Changed, so it fires when a note comes or goes and stays quiet when nothing changed.
    [Fact]
    public void Changed_fires_only_when_the_notes_change()
    {
        var links = new OpenedLinks();
        var pulses = 0;
        using var _ = links.Changed.Subscribe(_ => pulses++);

        links.Note(First, Edge);
        links.Note(First, Edge);
        links.Forget(Other.Handle);
        links.Forget(Second);
        Assert.Equal(1, pulses);

        links.Note(First, Other);
        links.Forget(Other.Handle);
        Assert.Equal(3, pulses);
    }
}

// Which window a link went into (LinkWindowWatch.Receiver): the first browser window on the bookmark's desktop to come
// to the front within the wait, else the one in front when the wait ends, else nothing.
public sealed class LinkWindowWatchTests
{
    static readonly Guid Desk = Guid.NewGuid();
    static readonly Guid OtherDesk = Guid.NewGuid();

    readonly Subject<WindowHandle> foreground = new();
    readonly Subject<System.Reactive.Unit> deadline = new();
    // What each fake window is: its process and the desktop it sits on.
    readonly Dictionary<WindowHandle, (int Id, string Name, Guid Desktop)> windows = new()
    {
        [new WindowHandle(1)] = (10, "Code", Desk),
        [new WindowHandle(2)] = (20, "msedge", OtherDesk),
        [new WindowHandle(3)] = (30, "msedge", Desk),
        [new WindowHandle(4)] = (40, "firefox", Desk),
        [new WindowHandle(5)] = (50, "chrome", Desk),
    };
    readonly List<WindowHandle> desktopAsked = [];
    Maybe<WindowHandle> inFront = Maybe<WindowHandle>.None;

    List<LinkWindow> Watch()
    {
        var noted = new List<LinkWindow>();
        LinkWindowWatch.Receiver(foreground, deadline, () => inFront,
                w => windows.TryGetValue(w, out var x) ? (x.Id, x.Name) : Maybe<(int, string)>.None,
                w =>
                {
                    desktopAsked.Add(w);
                    return windows.TryGetValue(w, out var x) ? x.Desktop : Maybe<Guid>.None;
                },
                Desk)
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

    // The desktop is a cross-process COM call: asked only of a window that is a browser at all.
    [Fact]
    public void Only_a_browser_window_is_asked_its_desktop()
    {
        Watch();

        foreground.OnNext(new WindowHandle(1));
        foreground.OnNext(new WindowHandle(2));

        Assert.Equal([new WindowHandle(2)], desktopAsked);
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
}
