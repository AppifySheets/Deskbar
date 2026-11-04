using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// What quick find can start (spec, "Launching"). Built from the roster and the Start menu, with the shapes the
// builder check found in Petre's real state.json: eleven Teams entries that differ only in session arguments,
// packaged apps stored under versioned WindowsApps paths, and Rider entries that name a solution each.
public class LaunchCatalogTests
{
    const string Teams = @"C:\Program Files\WindowsApps\MSTeams_25198.1112.3855.2_x64__8wekyb3d8bbwe\ms-teams.exe";
    const string Rider = @"C:\Program Files\JetBrains\JetBrains Rider 2024.3.2\bin\rider64.exe";
    const string Photos = @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_2025.11010.4001.0_x64__8wekyb3d8bbwe\Photos.exe";
    const string Notepad = @"C:\Program Files\Notepad++\notepad++.exe";

    static readonly FindPlace Here = new(Guid.Empty, "Here");

    static readonly IReadOnlySet<string> Existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { @"C:\src\X.sln", @"C:\src\Y.sln" };

    static IReadOnlyList<FindResult> Catalog(
        IReadOnlyList<InventoryEntry> roster,
        IReadOnlyList<StartMenuApp>? startMenu = null,
        IReadOnlyList<WindowInfo>? running = null) =>
        LaunchCatalog.Of(roster, startMenu ?? [], running ?? [], Existing.Contains, (_, _) => Here);

    static WindowInfo Running(string path, string? commandLine = null) =>
        new(new WindowHandle(7), 1, Path.GetFileNameWithoutExtension(path), path, "x", commandLine);

    [Fact]
    public void Many_session_variants_of_one_program_are_one_row()
    {
        var roster = Enumerable.Range(0, 11)
            .Select(i => new InventoryEntry(Teams, $"\"{Teams}\" --update-trigger --restart-count={i}", "Teams"))
            .ToList();
        var startMenu = new StartMenuApp("Microsoft Teams", "MSTeams_8wekyb3d8bbwe!MSTeams", null);

        var row = Assert.Single(Catalog(roster, [startMenu]));

        Assert.Equal(FindKind.Remembered, row.Kind);
        Assert.Equal("Microsoft Teams", row.Title);
        Assert.Equal("explorer.exe", row.Launch!.Launch.Program);
        Assert.Equal([@"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams"], row.Launch.Launch.Arguments);
    }

    [Fact]
    public void A_content_argument_makes_its_own_row()
    {
        var found = Catalog(
        [
            new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\X.sln", "X"),
            new InventoryEntry(Rider, $"\"{Rider}\" \"C:\\src\\Y.sln\"", "Y"),
        ]);

        Assert.Equal(["rider64", "X.sln", "Y.sln"], found.Select(r => r.Title).Order());
        Assert.Equal([@"C:\src\X.sln"], found.Single(r => r.Title == "X.sln").Launch!.Launch.Arguments);
        Assert.Empty(found.Single(r => r.Title == "rider64").Launch!.Launch.Arguments);
        Assert.All(found, r => Assert.Equal("rider64", r.Launch!.ExpectedProcess));
    }

    [Fact]
    public void A_running_program_has_no_row() =>
        Assert.Empty(Catalog(
            [new InventoryEntry(Notepad, null, "notes")],
            [new StartMenuApp("Notepad++", @"{6D809377}\Notepad++\notepad++.exe", Notepad)],
            [Running(Notepad)]));

    [Fact]
    public void Open_content_hides_only_its_own_row()
    {
        var found = Catalog(
            [
                new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\X.sln", "X"),
                new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\Y.sln", "Y"),
            ],
            running: [Running(Rider, $"\"{Rider}\" C:\\src\\X.sln")]);

        Assert.Equal(["Y.sln"], found.Select(r => r.Title));
    }

    [Fact]
    public void A_packaged_path_launches_through_its_start_menu_entry()
    {
        var withEntry = Assert.Single(Catalog(
            [new InventoryEntry(Photos, "\"Photos.exe\" ms-photos:spareprocess-viewer", "Photos")],
            [new StartMenuApp("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", null)]));
        Assert.Equal([@"shell:AppsFolder\Microsoft.Windows.Photos_8wekyb3d8bbwe!App"], withEntry.Launch!.Launch.Arguments);
        Assert.Null(withEntry.Launch.ExpectedProcess);

        Assert.Empty(Catalog([new InventoryEntry(Photos, null, "Photos")]));
    }

    [Fact]
    public void A_frame_host_entry_has_no_row() =>
        Assert.Empty(Catalog([new InventoryEntry(@"C:\Windows\System32\ApplicationFrameHost.exe", "ApplicationFrameHost.exe -Embedding", "Calculator")]));

    [Fact]
    public void A_start_menu_app_already_covered_by_the_roster_is_listed_once()
    {
        var found = Catalog(
            [new InventoryEntry(Notepad, null, "notes")],
            [new StartMenuApp("Notepad++", @"{6D809377}\Notepad++\notepad++.exe", Notepad)]);

        var row = Assert.Single(found);
        Assert.Equal(FindKind.Remembered, row.Kind);
        Assert.Equal("Notepad++", row.Title);
    }

    [Fact]
    public void A_start_menu_app_never_used_is_listed_from_the_start_menu()
    {
        var row = Assert.Single(Catalog([], [new StartMenuApp("Notepad++", @"{6D809377}\Notepad++\notepad++.exe", Notepad)]));
        Assert.Equal(FindKind.StartMenu, row.Kind);
        Assert.Equal(Notepad, row.Launch!.Launch.Program);
        Assert.Equal("notepad++", row.Launch.ExpectedProcess);
    }

    [Fact]
    public void Start_menu_entries_that_are_not_programs_are_left_out() =>
        Assert.Empty(Catalog([],
        [
            new StartMenuApp("Uninstall Notepad++", @"{6D809377}\Notepad++\uninstall.exe", @"C:\Program Files\Notepad++\uninstall.exe"),
            new StartMenuApp("Notepad++ Help", @"{6D809377}\Notepad++\help.chm", @"C:\Program Files\Notepad++\help.chm"),
        ]));

    [Fact]
    public void Launch_rows_say_not_running_and_where()
    {
        var row = Assert.Single(Catalog([new InventoryEntry(Notepad, null, "notes")]));
        Assert.Contains("not running", row.Detail);
        Assert.Equal(Here, row.Place);
        Assert.Contains("not running", row.SearchText);
    }
}
