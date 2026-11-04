using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// The tab reader keeps each tab's title for quick find, cleaned of what the app appends to its accessible
// name. The suffixes are the ones measured on Petre's machine
// (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
public class BrowserTabNamesTests
{
    [Theory]
    [InlineData("Invoice 2026-10 - Audio playing", "Invoice 2026-10")]
    [InlineData("Invoice 2026-10 - Audio muted", "Invoice 2026-10")]
    [InlineData("Microsoft Partner - Sleeping", "Microsoft Partner")]
    [InlineData("New tab - Discarded", "New tab")]
    [InlineData("Acquisitions | Partner Center - Part of tab group Store", "Acquisitions | Partner Center")]
    [InlineData("Running apps quick find, Editor Group 1", "Running apps quick find")]
    [InlineData("App code signing, Editor Group 12", "App code signing")]
    [InlineData("Invoice 2026-10", "Invoice 2026-10")]
    public void A_tab_title_drops_what_the_app_appends(string name, string title) =>
        Assert.Equal(title, BrowserTabs.TabTitle(name));

    // A page whose own title ends in a word Edge also uses must survive when nothing was appended.
    [Fact]
    public void A_title_that_only_contains_a_state_word_is_left_alone() =>
        Assert.Equal("Sleeping bags", BrowserTabs.TabTitle("Sleeping bags"));

    // Selecting a tab re-finds it at the index quick find listed it under and checks its title first: tabs
    // move and close between the reading and Enter, and selecting whatever now sits at that index would take
    // Petre to the wrong page.
    [Theory]
    [InlineData("Invoice 2026-10", "Invoice 2026-10 - Sleeping", true)]
    [InlineData("app.cs", "app.cs, Editor Group 2", true)]
    [InlineData("Invoice 2026-10", "Mail", false)]
    public void Select_checks_the_tab_at_that_index_is_still_the_one(string listed, string accessibleName, bool same) =>
        Assert.Equal(same, BrowserTabs.SameTab(listed, accessibleName));

    [Fact]
    public void No_names_before_the_first_reading()
    {
        using var tabs = new BrowserTabs();
        Assert.Empty(tabs.NamesOf(new WindowHandle(0x1234)));
    }
}
