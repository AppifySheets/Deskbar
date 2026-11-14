using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// Everything open that quick find can list, each with where it is (spec, "What it searches" and "Every result
// says where it is").
public class OpenCatalogTests
{
    static readonly Guid Accounting = Guid.NewGuid(), Home = Guid.NewGuid();

    static WindowInfo Window(nint hwnd, string process, string title) =>
        new(new WindowHandle(hwnd), 1, process, $@"C:\apps\{process}.exe", title, null);

    static IReadOnlyList<FindResult> Catalog(
        IReadOnlyList<WindowInfo> windows,
        IReadOnlyDictionary<nint, IReadOnlyList<string>>? tabs = null,
        IReadOnlyDictionary<nint, string>? originals = null) =>
        OpenCatalog.Of(
            windows,
            h => h.Value % 2 == 0 ? new FindPlace(Accounting, "Accounting") : new FindPlace(Home, "Home"),
            h => tabs is not null && tabs.TryGetValue(h.Value, out var names) ? names : [],
            w => w.ProcessName == "msedge",
            h => originals is not null && originals.TryGetValue(h.Value, out var t) ? Maybe.From(t) : Maybe<string>.None);

    [Fact]
    public void Every_window_is_listed_with_its_place()
    {
        var found = Catalog([Window(2, "notepad", "Notes"), Window(3, "notepad", "Plans")]);
        Assert.Equal([FindKind.Window, FindKind.Window], found.Select(r => r.Kind));
        Assert.Equal(["Accounting", "Home"], found.Select(r => r.Place.Label));
        Assert.Equal(new WindowHandle(2), found[0].Window);
    }

    [Fact]
    public void A_browser_window_with_tabs_is_listed_by_its_tabs_and_stands_behind_them()
    {
        var all = Catalog(
            [Window(2, "msedge", "Invoice - Microsoft Edge")],
            new Dictionary<nint, IReadOnlyList<string>> { [2] = ["Invoice", "Mail"] });
        // The window is there only for a search that finds none of its tabs (FindRanking drops it otherwise).
        Assert.True(Assert.Single(all, r => r.Kind == FindKind.Window).TabsStandIn);
        var found = all.Where(r => r.Kind == FindKind.Tab).ToList();
        Assert.Equal(["Invoice", "Mail"], found.Select(r => r.Title));
        Assert.Equal([0, 1], found.Select(r => r.TabIndex!.Value));
        Assert.All(found, r => Assert.Equal("Invoice - Microsoft Edge", r.Detail));
        Assert.All(found, r => Assert.Equal(new WindowHandle(2), r.Window));
    }

    [Fact]
    public void A_browser_window_without_a_reading_is_listed_as_a_window() =>
        Assert.Equal(FindKind.Window, Assert.Single(Catalog([Window(2, "msedge", "YouTube Music")])).Kind);

    [Fact]
    public void A_vs_code_window_is_listed_and_so_are_its_tabs()
    {
        var found = Catalog(
            [Window(2, "Code", "app.cs - billing - Visual Studio Code")],
            new Dictionary<nint, IReadOnlyList<string>> { [2] = ["app.cs", "README.md"] });
        Assert.Equal([FindKind.Window, FindKind.Tab, FindKind.Tab], found.Select(r => r.Kind));
    }

    [Fact]
    public void A_renamed_window_is_found_by_its_original_title_too()
    {
        var found = Catalog(
            [Window(2, "Code", "VS")],
            originals: new Dictionary<nint, string> { [2] = "app.cs - billing - Visual Studio Code" });
        Assert.True(FindQuery.Parse("billing").Matches(Assert.Single(found).SearchText));
    }

    [Fact]
    public void Search_text_includes_the_place()
    {
        var found = Catalog(
            [Window(2, "msedge", "Invoice - Microsoft Edge")],
            new Dictionary<nint, IReadOnlyList<string>> { [2] = ["Invoice"] });
        Assert.True(FindQuery.Parse("acc inv").Matches(Assert.Single(found, r => r.Kind == FindKind.Tab).SearchText));
    }

    [Fact]
    public void Keys_are_stable_and_distinct()
    {
        var found = Catalog(
            [Window(2, "Code", "billing - Visual Studio Code")],
            new Dictionary<nint, IReadOnlyList<string>> { [2] = ["a.cs", "a.cs"] });
        Assert.Equal(found.Count, found.Select(r => r.Key).Distinct().Count());
        Assert.Equal("w:2", found[0].Key);
    }
}
