namespace Deskbar.Core.Updates;

// What a TEST BUILD says about itself, and which installed release it can hand over to.
//
// Deskbar task 7dd83c89 (https://deskbar.app/task#7dd83c89-2f70-4852-bbed-86d356d4472f): Petre asked why he
// got no update. The copy on his screen was a Debug build a session had started from its worktree to show
// him a change, and since the release workflow began numbering each release one above the last (PR 510),
// a build that is not a release keeps the git height as its patch number. So that copy called itself
// 2.6.208 while the newest release was 2.6.58, the update check compared the two as numbers, found itself
// ahead, and wrote "is current" every five minutes all night. Nothing on screen said it was a test build.
//
// His answer: "let there be a clear marking that it's a test build somewhere and offer a restart to a
// regular build in the context menu". So a test build names itself on the tray icon's tooltip and at the
// top of both menus (the tray's and the bar's background menu), and both menus offer a restart into the
// newest installed release, which then updates itself the ordinary way.
//
// Only the decisions live here: the name to show and which exe is the newest release. Telling a test
// build apart (an exe that is not single-file published, and not the Store package), listing the folders
// and the restart itself are in Deskbar.App.
public static class TestBuild
{
    // The name a test build goes by: its worktree's, when it was built in one. Every session builds in
    // E:\repos\TaskSpaces\.claude\worktrees\<topic>\src\Deskbar.App\bin\..., so the folder after
    // "worktrees" is the topic, the one word that tells Petre which session's change is on his screen.
    // Outside a worktree (the main checkout, a scratch copy) the exe's folder path says more than any
    // word picked out of it, so the caller shows the folder instead and this returns null.
    public static string? WorktreeOf(string exePath)
    {
        var parts = exePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindLastIndex(parts, part => part.Equals("worktrees", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < parts.Length - 1 ? parts[at + 1] : null;
    }

    // The line both menus and the tooltip open with: "Test build 2.6.208 (add-box-skills)". The version is
    // the display one (no "+commit"), because the number is what makes it recognisable next to a release.
    public static string Label(string displayVersion, string exePath) =>
        WorktreeOf(exePath) is { } worktree
            ? $"Test build {displayVersion} ({worktree})"
            : $"Test build {displayVersion}";

    // The newest installed release among the given exe paths: the highest Deskbar-<x.y.z>-win-x64.exe by
    // version, compared as numbers (2.6.58 above 2.6.9). Anything else in the folders (a development
    // Deskbar.exe, a renamed copy, a .part download) is not a release and is never picked, by the same
    // name rule that decides what OldVersions may delete. Null when there is none, and then the menus
    // carry the marking alone, without a restart item that would have nothing to start.
    public static string? NewestRelease(IEnumerable<string> exePaths) =>
        exePaths
            .Select(path => (Path: path, Version: OldVersions.VersionOf(System.IO.Path.GetFileName(path))))
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();

    // The version of a release exe path, for the restart item's label ("Restart into Deskbar 2.6.58").
    public static string? VersionOf(string exePath) =>
        OldVersions.VersionOf(System.IO.Path.GetFileName(exePath))?.ToString();
}
