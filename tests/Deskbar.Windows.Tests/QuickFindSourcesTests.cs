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

    [Fact]
    public void A_vs_code_tab_row_selects_by_the_editor_rule() =>
        Assert.True(QuickFindSources.TabToSelect(
            new FindResult("t:1:0:app.cs", FindKind.Tab, "app.cs", Somewhere, "app.cs", Window: new WindowHandle(1), TabIndex: 0), _ => "Code")!.Value.Code);
}
