using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Licensing;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's Deskbar Pro tab (ManageWindow.Pro.cs), against the real window as the other Manage tests are, because
// what could break is the wiring: ProTab's strings reaching the controls, and the buttons reaching App's actions.
// What the tab says in each state is ProTab's job and is settled in Core (ProTabTests). A test process is not
// packaged and has no Pro, so the state here is always the GitHub copy's.
public class ManageProTabTests
{
    static WorkspaceManager Built()
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = AppState.Empty });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // Counts what each action was asked to do.
    sealed class Calls
    {
        public int GetPro, OpenStore, Report, CheckAgain, Price;

        public ManageWindow.ProActions Actions() => new(
            () => GetPro++, () => OpenStore++, () => Report++,
            () => { CheckAgain++; return Task.CompletedTask; },
            () => { Price++; return Task.FromResult(Result.Success("$18.99")); });
    }

    static T Named<T>(ManageWindow window, string name) => (T)window.FindName(name)!;

    static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // The sentence as a reader sees it: the word joiners that keep the price on one line are zero width, so they
    // are taken out before comparing.
    static string Pitch(ManageWindow window) => new TextRange(
        Named<TextBlock>(window, "ProPitch").ContentStart, Named<TextBlock>(window, "ProPitch").ContentEnd).Text.Replace("⁠", "");

    [Fact]
    public void The_tab_is_last_and_says_free_for_personal_use() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(), compatibilityMode: false, pro: new Calls().Actions());

        var item = Named<TabItem>(window, "ProTabItem");
        var tabs = (TabControl)item.Parent;
        Assert.Same(item, tabs.Items[tabs.Items.Count - 1]);
        Assert.Equal("Deskbar Pro", item.Header);
        Assert.Equal("This copy: free for personal use", Named<TextBlock>(window, "ProStatus").Text);
        Assert.StartsWith(ProTab.PitchLead, Pitch(window));
        Assert.Contains(ProTab.ListPrice + ProTab.PitchTail, Pitch(window));

        window.Close();
    });

    // The GitHub copy's button opens the Store page and never starts a purchase; Check again is not offered,
    // because there is no Store licence to read; and the price is not asked for.
    [Fact]
    public void GitHub_copy_button_opens_the_store() => StaThread.Run(() =>
    {
        var calls = new Calls();
        var window = new ManageWindow(Built(), compatibilityMode: false, pro: calls.Actions());

        Assert.Equal("Open Deskbar in the Store", Named<TextBlock>(window, "ProBuyText").Text);
        Click(Named<Button>(window, "ProBuy"));

        Assert.Equal(1, calls.OpenStore);
        Assert.Equal(0, calls.GetPro);
        Assert.Equal(0, calls.Price);
        Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "ProCheckRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<TextBlock>(window, "ProStoreNote").Visibility);

        window.Close();
    });

    [Fact]
    public void Report_a_problem_reaches_app() => StaThread.Run(() =>
    {
        var calls = new Calls();
        var window = new ManageWindow(Built(), compatibilityMode: false, pro: calls.Actions());

        Click(Named<Button>(window, "ProReport"));

        Assert.Equal(1, calls.Report);

        window.Close();
    });

    // Built without App's actions (the other Manage tests' shape): the tab still paints and its buttons do nothing.
    [Fact]
    public void Without_actions_the_buttons_do_nothing() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(), compatibilityMode: false);

        Click(Named<Button>(window, "ProBuy"));
        Click(Named<Button>(window, "ProReport"));
        Assert.Equal("This copy: free for personal use", Named<TextBlock>(window, "ProStatus").Text);

        window.Close();
    });
}
