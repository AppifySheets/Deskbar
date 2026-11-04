using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;

namespace Deskbar.Windows.Tests;

// Quick find's window and its controller (docs/superpowers/specs/2026-11-04-quick-find-design.md). The window
// is display only; the controller decides what is listed and what Enter does. Built on an STA thread like
// every other WPF window test here.
public class QuickFindWindowTests
{
    static readonly Guid Accounting = Guid.NewGuid();

    static FindResult Window(string key, string title, nint hwnd) =>
        new(key, FindKind.Window, title, new FindPlace(Accounting, "Accounting"), $"{title} Accounting", Window: new WindowHandle(hwnd));

    static QuickFind Controller(QuickFindWindow window, IReadOnlyList<FindResult> catalog, Func<FindResult, Result> go) =>
        new(window, () => catalog, () => [Accounting], go, _ => "#5B6CFF", _ => null);

    [Fact]
    public void Typing_lists_matches_and_highlights_the_first() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice 2026-10", 1), Window("w:2", "Notes", 2)], _ => Result.Success());

        find.Show("inv");

        Assert.Equal(["w:1"], window.Keys);
        Assert.Equal("w:1", window.Highlighted);
        window.Dismiss();
    });

    [Fact]
    public void Escape_dismisses_and_enter_chooses_the_highlighted_key() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        var went = new List<string>();
        using var find = Controller(window, [Window("w:1", "Invoice a", 1), Window("w:2", "Invoice b", 2)], r => { went.Add(r.Key); return Result.Success(); });

        find.Show("inv");
        window.Press(Key.Down);
        window.Press(Key.Enter);
        Assert.Equal(["w:2"], went);
        Assert.False(window.IsOpen);

        find.Show("inv");
        window.Press(Key.Escape);
        Assert.False(window.IsOpen);
        Assert.Single(went);
    });

    // The plan's Review Focus, item 1: the window closed between the search and Enter.
    [Fact]
    public void Going_to_a_closed_window_fails_without_switching() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice a", 1), Window("w:2", "Invoice b", 2)],
            r => r.Key == "w:1" ? Result.Failure("That window has closed.") : Result.Success());

        find.Show("inv");
        window.Press(Key.Enter);

        Assert.True(window.IsOpen);
        Assert.Equal(["w:2"], window.Keys);
        Assert.Equal("w:2", window.Highlighted);
        window.Dismiss();
    });

    [Fact]
    public void Results_sit_under_their_workspace_header() => StaThread.Run(() =>
    {
        var window = new QuickFindWindow();
        using var find = Controller(window, [Window("w:1", "Invoice", 1)], _ => Result.Success());

        find.Show("inv");

        Assert.Equal(["Accounting"], window.Headers);
        window.Dismiss();
    });
}
