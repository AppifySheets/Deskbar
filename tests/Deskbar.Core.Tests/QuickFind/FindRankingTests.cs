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
