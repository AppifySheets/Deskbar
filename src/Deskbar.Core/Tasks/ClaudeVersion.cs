using System.Text.RegularExpressions;

namespace Deskbar.Core.Tasks;

// WHETHER THIS MACHINE'S CLAUDE CODE HONOURS "asyncRewake", read from `claude --version` ("2.1.331 (Claude Code)").
//
// The comment waiter (a comment wakes its session; plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md) is a
// Stop hook that waits, sometimes for days. Claude Code runs it in the background only because its entry says
// "asyncRewake": true. A build that did not know the option would run it as an ordinary Stop hook and hold its session
// after every turn until a comment came or a month passed. So the entry is written only for a version known to have it
// (the final review of that branch, I6): 2.1.322, the oldest build on this machine on 2026-11-24, already did (so did
// 2.1.330, which its VS Code sessions run, and 2.1.331, on which it was measured). Anything that does not read as a
// version is "not known", and gets no waiter: a comment then reaches its session at its next turn or by a resume.
public static class ClaudeVersion
{
    /// <summary>The oldest Claude Code known to honour asyncRewake (see the header).</summary>
    public static readonly Version AsyncRewakeSince = new(2, 1, 322);

    static readonly Regex Leading = new(@"^\s*(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    public static bool SupportsAsyncRewake(string versionOutput) => AtLeast(versionOutput, AsyncRewakeSince);

    /// <summary>
    /// The oldest Claude Code whose PermissionRequest hook was measured to work as Deskbar uses it (Allow / Deny from a
    /// task's page; the live spike of 2026-11-26 ran 2.1.332: allow, deny, setMode, and the dialog drawn alongside). An
    /// older build gets no permission entry, so nothing new is written into a settings file it may not read the same way.
    /// </summary>
    public static readonly Version PermissionHookSince = new(2, 1, 332);

    public static bool SupportsPermissionHook(string versionOutput) => AtLeast(versionOutput, PermissionHookSince);

    static bool AtLeast(string versionOutput, Version since) =>
        Leading.Match(versionOutput ?? "") is { Success: true } m
        && new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) >= since;
}
