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
public static class LaunchEnvironment
{
    // Whether a variable belongs to an Electron or VS Code host process and must not reach Deskbar's children.
    internal static bool IsHostOnly(string name) =>
        name.Equals("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase);

    // Removes them from this process, which is the block every later start inherits.
    public static void Clean() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(IsHostOnly)
            .ToList()
            .ForEach(name => Environment.SetEnvironmentVariable(name, null));
}
