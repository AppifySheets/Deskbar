using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;

namespace TaskSpaces.Windows.Tests;

// The hover card's picture is for a window you cannot see. Petre: "preview when hovering over icons should
// only work for non-current workspace". A window in the workspace you are standing in is already on screen,
// so its card is text alone, and the capture (the expensive half of the card) is never made.
public class FloatingBarHoverCardTests
{
    // A real, painted window: WindowPreview captures with PrintWindow, which hands back nothing useful for a
    // window that has not composed a frame yet. Same wait as WindowPreviewTests, for the same reason.
    static Window Shown()
    {
        var window = new Window
        {
            Title = "hover card target",
            Width = 200, Height = 120, Left = 40, Top = 40,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Content = new Border { Background = Brushes.Lime },
        };
        var rendered = false;
        window.ContentRendered += (_, _) => rendered = true;
        window.Show();
        window.UpdateLayout();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!rendered && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
            Thread.Sleep(10);
        }
        Assert.True(rendered, "the window never painted a frame within 10s");
        return window;
    }

    static WindowRow RowFor(Window window) =>
        new(new WindowInfo(new WindowHandle(new WindowInteropHelper(window).Handle), 1, "target", @"C:\Apps\target.exe", window.Title, null),
            Maybe<string>.None);

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static bool HasPicture(StackPanel card) => Descendants(card).OfType<Image>().Any();

    static IEnumerable<string> Texts(StackPanel card) => Descendants(card).OfType<TextBlock>().Select(t => t.Text);

    [Fact]
    public void A_window_in_another_workspace_gets_a_picture() => StaThread.Run(() =>
    {
        var window = Shown();

        var card = FloatingBar.HoverCard("Sparrow", RowFor(window), withPicture: true);

        Assert.True(HasPicture(card));
        window.Close();
    });

    [Fact]
    public void A_window_in_the_current_workspace_gets_its_text_and_no_picture() => StaThread.Run(() =>
    {
        var window = Shown();

        var card = FloatingBar.HoverCard("GEPHA", RowFor(window), withPicture: false);

        Assert.False(HasPicture(card));
        Assert.Contains(Texts(card), text => text.Contains("hover card target"));
        Assert.Contains(Texts(card), text => text.Contains("GEPHA"));
        window.Close();
    });
}
