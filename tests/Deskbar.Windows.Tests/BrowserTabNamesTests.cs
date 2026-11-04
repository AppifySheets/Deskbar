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

    [Fact]
    public void No_names_before_the_first_reading()
    {
        using var tabs = new BrowserTabs();
        Assert.Empty(tabs.NamesOf(new WindowHandle(0x1234)));
    }
}
