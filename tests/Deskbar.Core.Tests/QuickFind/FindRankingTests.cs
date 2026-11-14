using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// The order quick find lists matches in (spec, "Ranking"): open things before anything that would have to be
// started, so Enter never starts a second copy by accident; then a title that starts with what was typed; then
// the workspace used most recently.
public class FindRankingTests
{
    static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    static FindResult Row(string key, FindKind kind, string title, Guid? workspace = null) =>
        new(key, kind, title, new FindPlace(workspace, workspace is null ? "unnamed desktop" : "ws"), title);

    static IReadOnlyList<string> Keys(string typed, IReadOnlyList<FindResult> all, params Guid[] recent) =>
        FindRanking.Rank(FindQuery.Parse(typed), all, recent).Select(r => r.Key).ToList();

    [Fact]
    public void Running_things_come_before_anything_that_would_start() =>
        Assert.Equal(["window", "remembered", "startmenu"], Keys("rider",
        [
            Row("startmenu", FindKind.StartMenu, "JetBrains Rider"),
            Row("remembered", FindKind.Remembered, "Rider"),
            Row("window", FindKind.Window, "Rider X"),
        ]));

    [Fact]
    public void Tabs_rank_with_windows_not_after_them() =>
        Assert.Equal(["tab", "window", "remembered"], Keys("inv",
        [
            Row("remembered", FindKind.Remembered, "Invoicer"),
            Row("window", FindKind.Window, "My invoice list"),
            Row("tab", FindKind.Tab, "Invoice"),
        ]));

    [Fact]
    public void A_title_starting_with_the_first_piece_wins_inside_its_group() =>
        Assert.Equal(["starts", "contains"], Keys("inv",
        [
            Row("contains", FindKind.Window, "My invoices"),
            Row("starts", FindKind.Window, "Invoices 2026"),
        ]));

    [Fact]
    public void The_most_recently_used_workspace_comes_first() =>
        Assert.Equal(["on-b", "on-a"], Keys("notes",
        [
            Row("on-a", FindKind.Window, "Notes", A),
            Row("on-b", FindKind.Window, "Notes", B),
        ], B, A));

    // Petre: "search should show me most recently used bars first". Recency of the workspace outranks how well
    // the title matches, so the rows he was just in lead even when an older workspace has a better prefix.
    [Fact]
    public void A_recent_workspace_outranks_a_better_prefix_in_an_older_one() =>
        Assert.Equal(["recent-contains", "older-starts"], Keys("inv",
        [
            Row("older-starts", FindKind.Window, "Invoices 2026", A),
            Row("recent-contains", FindKind.Window, "My invoices", B),
        ], B, A));

    [Fact]
    public void Running_things_still_come_before_a_recent_workspaces_launch_rows() =>
        Assert.Equal(["older-window", "recent-launch"], Keys("inv",
        [
            Row("recent-launch", FindKind.Remembered, "Invoicer", B),
            Row("older-window", FindKind.Window, "Invoices", A),
        ], B, A));

    // Review I6: launch rows are capped, so a broad query does not draw hundreds of programs; open things are
    // never capped.
    [Fact]
    public void Launch_rows_are_capped_and_open_rows_are_not()
    {
        var all = Enumerable.Range(0, 40).Select(i => Row($"w{i}", FindKind.Window, $"app {i}"))
            .Concat(Enumerable.Range(0, 40).Select(i => Row($"s{i}", FindKind.StartMenu, $"app {i}")))
            .ToList();
        var ranked = FindRanking.Rank(FindQuery.Parse("app"), all, []);
        Assert.Equal(40, ranked.Count(r => r.IsOpen));
        Assert.Equal(FindRanking.MaxLaunchRows, ranked.Count(r => !r.IsOpen));
    }

    // Petre, with sixteen VS Code windows open: "when i search for code, deskbar gives me a bunch of nonsense",
    // then "i want just a new code window, i should be able to do that". Typing a program's own name while
    // several of its windows are open has no single window to go to, so the row that opens another leads.
    static FindResult NewWindow(string title, string exe, int open, Guid? workspace = null) =>
        new($"n:{title}", FindKind.NewWindow, title, new FindPlace(workspace, "ws"), $"{title} {exe}", OpenWindows: open);

    [Fact]
    public void Naming_a_program_with_several_windows_open_puts_its_new_window_row_first() =>
        Assert.Equal(["n:Visual Studio Code", "one", "two"], Keys("code",
        [
            Row("one", FindKind.Window, "a - Visual Studio Code"),
            Row("two", FindKind.Window, "b - Visual Studio Code"),
            NewWindow("Visual Studio Code", "Code", open: 2),
        ]));

    // The spec's anti-goal still holds where it can: with ONE window open, "go to it" has an answer, so Enter
    // goes there and the new window row waits below everything open.
    [Fact]
    public void With_one_window_open_the_window_still_leads() =>
        Assert.Equal(["only", "n:Notepad"], Keys("notepad",
        [
            NewWindow("Notepad", "notepad", open: 1),
            Row("only", FindKind.Window, "notes.txt - Notepad"),
        ]));

    // Part of a word is not the program's name: "inv" is looking for an invoice, not asking for another
    // Invoice Ninja window, so the open tab keeps the top row.
    [Fact]
    public void Part_of_a_programs_name_does_not_put_its_new_window_row_first() =>
        Assert.Equal(["tab", "n:Invoice Ninja"], Keys("inv",
        [
            NewWindow("Invoice Ninja", "ninja", open: 3),
            Row("tab", FindKind.Tab, "Invoice 2026-10"),
        ]));

    [Fact]
    public void A_new_window_row_comes_before_programs_that_are_not_running() =>
        Assert.Equal(["n:Code Runner", "remembered"], Keys("runner",
        [
            Row("remembered", FindKind.Remembered, "Road Runner"),
            NewWindow("Code Runner", "runner", open: 1),
        ]));

    // The nonsense itself: every tab of every VS Code window matched "code", because a tab is searched together
    // with the title of the window holding it and that title ends in "Visual Studio Code". A tab is only found
    // when something typed is in its OWN title or its workspace.
    [Fact]
    public void A_tab_is_not_found_through_its_windows_title_alone() =>
        Assert.Equal(["claude", "window"], Keys("code",
        [
            new FindResult("window", FindKind.Window, "app.cs - Deskbar - Visual Studio Code", new FindPlace(A, "ws"), "app.cs - Deskbar - Visual Studio Code ws"),
            new FindResult("app.cs", FindKind.Tab, "app.cs", new FindPlace(A, "ws"), "app.cs app.cs - Deskbar - Visual Studio Code ws"),
            new FindResult("claude", FindKind.Tab, "Claude Code", new FindPlace(A, "ws"), "Claude Code app.cs - Deskbar - Visual Studio Code ws"),
        ]).Order());

    [Fact]
    public void A_tab_is_still_found_by_its_window_and_its_own_title_together() =>
        Assert.Equal(["app.cs"], Keys("deskbar app",
        [
            new FindResult("app.cs", FindKind.Tab, "app.cs", new FindPlace(A, "ws"), "app.cs x - Deskbar - Visual Studio Code ws"),
            new FindResult("other", FindKind.Tab, "readme", new FindPlace(A, "ws"), "readme x - Deskbar - Visual Studio Code ws"),
        ]));

    [Fact]
    public void A_tab_is_found_by_its_workspace() =>
        Assert.Equal(["tab"], Keys("accounting",
        [
            new FindResult("tab", FindKind.Tab, "Inbox", new FindPlace(A, "Accounting"), "Inbox Mail - Microsoft Edge Accounting"),
        ]));

    // A browser window is listed through its tabs (OpenCatalog). When none of them is found but the window is,
    // typing "edge" say, the window itself is listed: one row per Edge window instead of every tab in them.
    [Fact]
    public void A_browser_window_is_listed_only_when_none_of_its_tabs_is_found()
    {
        var window = new FindResult("window", FindKind.Window, "Invoice - Microsoft Edge", new FindPlace(A, "ws"), "Invoice - Microsoft Edge ws",
            Window: new Deskbar.Core.Domain.WindowHandle(2), TabsStandIn: true);
        var tab = new FindResult("tab", FindKind.Tab, "Invoice", new FindPlace(A, "ws"), "Invoice Invoice - Microsoft Edge ws",
            Window: new Deskbar.Core.Domain.WindowHandle(2), TabIndex: 0);

        Assert.Equal(["tab"], Keys("inv", [window, tab]));
        Assert.Equal(["window"], Keys("edge", [window, tab]));
    }

    // The cap on rows that would start something keeps its place in the order: a leading new window row stays
    // first, and is counted.
    [Fact]
    public void The_cap_does_not_move_a_leading_new_window_row()
    {
        var all = Enumerable.Range(0, 40).Select(i => Row($"s{i}", FindKind.StartMenu, $"code {i}"))
            .Append(Row("w", FindKind.Window, "x - Visual Studio Code"))
            .Append(NewWindow("Visual Studio Code", "Code", open: 2))
            .ToList();
        var ranked = FindRanking.Rank(FindQuery.Parse("code"), all, []);
        Assert.Equal(["n:Visual Studio Code", "w"], ranked.Take(2).Select(r => r.Key));
        Assert.Equal(FindRanking.MaxLaunchRows, ranked.Count(r => !r.IsOpen));
    }

    [Fact]
    public void Only_matching_results_are_returned() =>
        Assert.Empty(Keys("zzz", [Row("w", FindKind.Window, "Notes")]));

    [Fact]
    public void The_search_text_is_what_is_matched_not_only_the_title() =>
        Assert.Equal(["tab"], Keys("acc inv",
        [
            new FindResult("tab", FindKind.Tab, "Invoice", new FindPlace(A, "Accounting"), "Invoice Accounting"),
        ]));
}
