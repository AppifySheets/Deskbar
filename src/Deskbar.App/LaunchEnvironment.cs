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
//
// NO_COLOR goes too, unless Windows' own environment sets it (Petre: "can we have more beautiful formatting than just
// text in the terminal?", task f8344a81). Measured 2026-11-23: a Deskbar started from a Claude session's shell carries
// NO_COLOR=1, and every window it opens on a session inherits it, because Windows Terminal gives a window started by
// `wt.exe` the CALLER's environment, not its own process's (probed: a marker variable set before `wt -w new` arrived in
// the new window). `claude attach` honours NO_COLOR and drew the session with 2 style codes instead of 706 (35 of them
// bold, 216 RGB colours), so every Claude window was one flat grey. The session itself is unaffected: Claude's daemon
// starts it with FORCE_COLOR=3, which outranks NO_COLOR, but the attach client in the window has no FORCE_COLOR.
// When the variable is set in the user's or the machine's environment it is Petre's own choice, not something a host
// passed along, so it stays and reaches every child as usual.
public static class LaunchEnvironment
{
    static readonly IReadOnlyList<string> ClaudeHostVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_JOB_DIR", "CLAUDE_CODE_CHILD_SESSION"];

    // Variables a host passes along that are also a person's real setting: dropped only when Windows does not set them.
    static readonly IReadOnlyList<string> HostUnlessSetInWindows = ["NO_COLOR"];

    // Whether a variable belongs to an Electron, VS Code or Claude Code host process and must not reach Deskbar's children.
    // setInWindows answers whether the user's or the machine's environment (the registry) sets a variable.
    internal static bool IsHostOnly(string name, Func<string, bool> setInWindows) =>
        name.Equals("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
        || ClaudeHostVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
        || (HostUnlessSetInWindows.Contains(name, StringComparer.OrdinalIgnoreCase) && !setInWindows(name));

    static bool SetInWindows(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is not null
        || Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) is not null;

    // Removes them from this process, which is the block every later start inherits.
    public static void Clean() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => IsHostOnly(name, SetInWindows))
            .ToList()
            .ForEach(name => Environment.SetEnvironmentVariable(name, null));
}
