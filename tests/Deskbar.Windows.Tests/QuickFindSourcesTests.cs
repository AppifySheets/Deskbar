using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;

namespace Deskbar.Windows.Tests;

// What Enter on a quick find row asks the tab reader to select: nothing for a window row, the listed tab for
// a tab row, read with VS Code's rule when the window is VS Code's.
public class QuickFindSourcesTests
{
    static readonly FindPlace Somewhere = new(null, "Desktop 1");

    [Fact]
    public void A_window_row_selects_no_tab() =>
        Assert.Null(QuickFindSources.TabToSelect(
            new FindResult("w:1", FindKind.Window, "Notes", Somewhere, "Notes", Window: new WindowHandle(1)), _ => "notepad"));

    [Fact]
    public void A_browser_tab_row_selects_that_tab_by_the_browser_rule()
    {
        var tab = QuickFindSources.TabToSelect(
            new FindResult("t:1:2:Inbox", FindKind.Tab, "Inbox", Somewhere, "Inbox", Window: new WindowHandle(1), TabIndex: 2), _ => "msedge");
        Assert.Equal((new WindowHandle(1), false, 2, "Inbox"), tab);
    }

    // Which roster arguments count as content is checked on the UI thread while the box refreshes, so a path on
    // a network share is never asked about: an offline share can block the check for seconds.
    [Theory]
    [InlineData(@"\\server\share\X.sln", false)]
    [InlineData(@"//server/share/X.sln", false)]
    [InlineData(@"C:\src\X.sln", true)]
    [InlineData(@"--update-trigger", false)]
    public void Only_local_rooted_paths_are_checked(string argument, bool asked)
    {
        var checkedPaths = new List<string>();
        QuickFindSources.LocalPathExists(argument, p => { checkedPaths.Add(p); return true; });
        Assert.Equal(asked, checkedPaths.Count == 1);
    }

    // Review M4: a mapped network drive letter is a share too, and is skipped the same way.
    [Fact]
    public void A_path_on_a_mapped_network_drive_is_not_checked()
    {
        var asked = false;
        QuickFindSources.LocalPathExists(@"Z:\work\X.sln", _ => asked = true, isNetworkDrive: root => root == @"Z:\");
        Assert.False(asked);
    }

    [Fact]
    public void A_vs_code_tab_row_selects_by_the_editor_rule() =>
        Assert.True(QuickFindSources.TabToSelect(
            new FindResult("t:1:0:app.cs", FindKind.Tab, "app.cs", Somewhere, "app.cs", Window: new WindowHandle(1), TabIndex: 0), _ => "Code")!.Value.Code);
}
