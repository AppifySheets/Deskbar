using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// What quick find can start (spec, "Launching"). Built from the roster and the Start menu, with the shapes found
// in Petre's real state.json and Start menu: eleven Teams entries that differ only in session arguments,
// packaged apps under versioned WindowsApps paths, Rider entries naming a solution each, Start menu shortcuts
// that share one exe but carry their own arguments (testnet wallets, every Developer Command Prompt on cmd.exe),
// and PWAs whose windows belong to the browser.
public class LaunchCatalogTests
{
    const string Teams = @"C:\Program Files\WindowsApps\MSTeams_25198.1112.3855.2_x64__8wekyb3d8bbwe\ms-teams.exe";
    const string Rider = @"C:\Program Files\JetBrains\JetBrains Rider 2024.3.2\bin\rider64.exe";
    const string NewRider = @"C:\Program Files\JetBrains\JetBrains Rider 2025.2\bin\rider64.exe";
    const string Photos = @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_2025.11010.4001.0_x64__8wekyb3d8bbwe\Photos.exe";
    const string Notepad = @"C:\Program Files\Notepad++\notepad++.exe";
    const string Cmd = @"C:\Windows\System32\cmd.exe";

    static readonly FindPlace Here = new(Guid.Empty, "Here");

    static readonly IReadOnlySet<string> Existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { @"C:\src\X.sln", @"C:\src\Y.sln", @"C:\data", Rider, NewRider, Notepad, Cmd };

    static IReadOnlyList<FindResult> Catalog(
        IReadOnlyList<InventoryEntry> roster,
        IReadOnlyList<StartMenuApp>? startMenu = null,
        IReadOnlyList<WindowInfo>? running = null,
        IReadOnlySet<string>? runningAppIds = null,
        IReadOnlySet<string>? starting = null,
        Func<string, string?, FindPlace>? destinationOf = null) =>
        LaunchCatalog.Of(new LaunchFacts(
            roster,
            startMenu ?? [],
            running ?? [],
            runningAppIds ?? new HashSet<string>(),
            starting ?? new HashSet<string>(),
            Existing.Contains,
            destinationOf ?? ((_, _) => Here),
            app => app.ParsingName.StartsWith("Petre.Deskbar_", StringComparison.Ordinal)));

    static WindowInfo Running(string path, string? commandLine = null) =>
        new(new WindowHandle(7), 1, Path.GetFileNameWithoutExtension(path), path, "x", commandLine);

    static StartMenuApp Entry(string name, string? target, string? parsing = null) =>
        new(name, parsing ?? $@"{{6D809377}}\{name}.lnk", target);

    [Fact]
    public void Many_session_variants_of_one_program_are_one_row()
    {
        var roster = Enumerable.Range(0, 11)
            .Select(i => new InventoryEntry(Teams, $"\"{Teams}\" --update-trigger --restart-count={i}", "Teams"))
            .ToList();

        var row = Assert.Single(Catalog(roster, [Entry("Microsoft Teams", null, "MSTeams_8wekyb3d8bbwe!MSTeams")]));

        Assert.Equal(FindKind.Remembered, row.Kind);
        Assert.Equal("Microsoft Teams", row.Title);
        Assert.Equal([@"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams"], row.Launch!.Launch.Arguments);
        // Held to the row's workspace by the exe the packaged app's window really belongs to (review I2).
        Assert.Equal("ms-teams", row.Launch.ExpectedProcess);
    }

    // Review C1: shortcuts that share an exe each keep their own row, launched through the shell by their own
    // entry so their arguments and working folder come along.
    [Fact]
    public void Start_menu_shortcuts_sharing_an_exe_each_keep_their_row_and_arguments()
    {
        var found = Catalog([], [Entry("Command Prompt", Cmd), Entry("Developer Command Prompt for VS 2022", Cmd)]);

        Assert.Equal(["Command Prompt", "Developer Command Prompt for VS 2022"], found.Select(r => r.Title).Order());
        Assert.All(found, r => Assert.Equal("explorer.exe", r.Launch!.Launch.Program));
        Assert.Equal([@"shell:AppsFolder\{6D809377}\Command Prompt.lnk"], found.Single(r => r.Title == "Command Prompt").Launch!.Launch.Arguments);
        Assert.All(found, r => Assert.Equal("cmd", r.Launch!.ExpectedProcess));
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
    }

    // Review M6: a solution is found by the folder it lives in, not only by its file name.
    [Fact]
    public void A_content_row_is_found_by_its_folder() =>
        Assert.True(FindQuery.Parse("src x").Matches(
            Catalog([new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\X.sln", "X")]).Single(r => r.Title == "X.sln").SearchText));

    // Review M5: an option's value is not content, and neither is an exe repeated on the line.
    [Fact]
    public void Option_values_and_exes_are_not_content() =>
        Assert.Equal(["notepad++"], Catalog([new InventoryEntry(Notepad, $"\"{Notepad}\" --datadir C:\\data \"{Notepad}\"", "n")]).Select(r => r.Title));

    // Review M1: the row's workspace comes from the entry's own command line, quoted or not.
    [Fact]
    public void A_content_rows_destination_uses_its_own_command_line()
    {
        var asked = new List<string?>();
        Catalog([new InventoryEntry(Rider, $"{Rider} C:\\src\\X.sln", "X")], destinationOf: (_, line) => { asked.Add(line); return Here; });
        Assert.Contains($"{Rider} C:\\src\\X.sln", asked);
    }

    [Fact]
    public void A_running_program_has_no_row() =>
        Assert.Empty(Catalog([new InventoryEntry(Notepad, null, "notes")], [Entry("Notepad++", Notepad)], [Running(Notepad)]));

    // Review I4: a newer version of the same program counts as running, and so does its open solution.
    [Fact]
    public void Another_version_of_the_program_counts_as_running() =>
        Assert.Empty(Catalog(
            [new InventoryEntry(Rider, $"\"{Rider}\" C:\\src\\X.sln", "X")],
            running: [Running(NewRider, $"\"{NewRider}\" C:\\src\\X.sln")]));

    // Review I3: a PWA's or a framed app's window belongs to another process, so it is known by its app id.
    [Fact]
    public void An_app_open_under_another_process_counts_as_running_by_its_app_id() =>
        Assert.Empty(Catalog([], [Entry("YouTube Music", null, "music.youtube.com-12345_abc!App")],
            runningAppIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "music.youtube.com-12345_abc!App" }));

    // Review I5: a program just started and still waiting for its window is not offered again.
    [Fact]
    public void A_program_still_starting_has_no_row() =>
        Assert.Empty(Catalog([new InventoryEntry(Notepad, null, "n")], starting: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notepad++" }));

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
        var row = Assert.Single(Catalog(
            [new InventoryEntry(Photos, "\"Photos.exe\" ms-photos:spareprocess-viewer", "Photos")],
            [Entry("Photos", null, "Microsoft.Windows.Photos_8wekyb3d8bbwe!App")]));
        Assert.Equal([@"shell:AppsFolder\Microsoft.Windows.Photos_8wekyb3d8bbwe!App"], row.Launch!.Launch.Arguments);
        Assert.Equal("Photos", row.Launch.ExpectedProcess);

        Assert.Empty(Catalog([new InventoryEntry(Photos, null, "Photos")]));
    }

    [Fact]
    public void A_program_that_is_gone_has_no_row() =>
        Assert.Empty(Catalog([new InventoryEntry(@"C:\Old\gone.exe", null, "gone")]));

    [Fact]
    public void A_frame_host_entry_has_no_row() =>
        Assert.Empty(Catalog([new InventoryEntry(@"C:\Windows\System32\ApplicationFrameHost.exe", "ApplicationFrameHost.exe -Embedding", "Calculator")]));

    [Fact]
    public void A_start_menu_app_already_covered_by_the_roster_is_listed_once()
    {
        var row = Assert.Single(Catalog([new InventoryEntry(Notepad, null, "notes")], [Entry("Notepad++", Notepad)]));
        Assert.Equal(FindKind.Remembered, row.Kind);
        Assert.Equal("Notepad++", row.Title);
    }

    [Fact]
    public void A_start_menu_app_never_used_is_listed_from_the_start_menu()
    {
        var row = Assert.Single(Catalog([], [Entry("Notepad++", Notepad)]));
        Assert.Equal(FindKind.StartMenu, row.Kind);
        Assert.Equal("notepad++", row.Launch!.ExpectedProcess);
    }

    [Fact]
    public void Start_menu_entries_that_are_not_programs_are_left_out() =>
        Assert.Empty(Catalog([],
        [
            Entry("Uninstall Notepad++", @"C:\Program Files\Notepad++\uninstall.exe"),
            Entry("Notepad++ Help", @"C:\Program Files\Notepad++\help.chm"),
        ]));

    // Review M7: starting Deskbar's own Store package here would perform its autostart takeover.
    [Fact]
    public void Deskbar_never_lists_itself() =>
        Assert.Empty(Catalog([], [Entry("Deskbar", null, "Petre.Deskbar_n8ch8v53xw03y!Deskbar")]));

    // Review I6: launch rows match on what they are called, not on "not running" or the workspace, so a
    // single common letter does not match every program on the machine.
    [Fact]
    public void Launch_rows_say_not_running_and_where_but_match_only_their_name()
    {
        var row = Assert.Single(Catalog([new InventoryEntry(Notepad, null, "notes")]));
        Assert.Contains("not running", row.Detail);
        Assert.Equal(Here, row.Place);
        Assert.DoesNotContain("not running", row.SearchText);
        Assert.DoesNotContain("Here", row.SearchText);
    }
}
