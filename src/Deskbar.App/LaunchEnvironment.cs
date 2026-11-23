namespace Deskbar.App;

// THE ENVIRONMENT DESKBAR HANDS ON. Every program Deskbar starts (a pinned app, Task Manager, a downloaded
// update) inherits Deskbar's own environment block, so whatever Deskbar inherited it passes along.
//
// Petre: "clicking vscode in quick launch doesn't open it". Measured: the pin was started and reported
// success, and no window appeared, because that Deskbar had been started from a terminal inside VS Code and
// carried ELECTRON_RUN_AS_NODE=1, which VS Code's extension host sets for itself. With that variable an
// Electron app (VS Code, Obsidian, Teams, Beeper) runs as a bare Node.js script, does nothing and exits.
// With it removed, the same start opened a VS Code window at once. The VSCODE_* variables are the extension
// host's too (its IPC pipe, its process id, its code cache) and mean nothing to a program Deskbar starts.
//
// A Deskbar started at login, from Explorer, never has these; removing them makes every other way of
// starting it (a dev build from a terminal, a script, an agent) behave the same.
//
// Claude Code's own go too (Task 9 of docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md,
// Global Constraints): a Deskbar started from a Claude session carries CLAUDECODE, CLAUDE_CODE_ENTRYPOINT and
// CLAUDE_CODE_SESSION_ATTENDED, and a `claude` that inherits them thinks it is a nested session. One started from a
// BACKGROUND session's shell also carries CLAUDE_JOB_DIR and CLAUDE_CODE_CHILD_SESSION (final review, item 10):
// CLAUDE_JOB_DIR names that session's job folder, and a hook of a session Deskbar started would pass it on as
// --job, where ClaudeHookEvent only trusts a job folder naming the hook's own session, so it would at best be noise;
// CLAUDE_CODE_CHILD_SESSION marks the process as a child of a session, which a session Deskbar starts is not.
// ClaudeCli strips them per child, but the terminal on a session (`wt ... claude attach`) is started through the
// shell, which cannot leave a variable out of one child's block, so they leave Deskbar's own block here instead.
// CLAUDE_PROJECT_DIR is not one of them: Claude Code sets it for hooks, never for Deskbar.
public static class LaunchEnvironment
{
    static readonly IReadOnlyList<string> ClaudeHostVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_JOB_DIR", "CLAUDE_CODE_CHILD_SESSION"];

    // Whether a variable belongs to an Electron, VS Code or Claude Code host process and must not reach Deskbar's children.
    internal static bool IsHostOnly(string name) =>
        name.Equals("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
        || ClaudeHostVariables.Contains(name, StringComparer.OrdinalIgnoreCase);

    // Removes them from this process, which is the block every later start inherits.
    public static void Clean() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(IsHostOnly)
            .ToList()
            .ForEach(name => Environment.SetEnvironmentVariable(name, null));
}
