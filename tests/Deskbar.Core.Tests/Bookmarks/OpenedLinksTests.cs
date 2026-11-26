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
