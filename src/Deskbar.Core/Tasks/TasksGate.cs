namespace Deskbar.Core.Tasks;

// The single switch for gating tasks behind Deskbar Pro, should that ever be decided
// (docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md). Today tasks are free for everyone, so
// callers ask Allowed(...) and a later change is one constant rather than a hunt through the UI.
public static class TasksGate
{
    /// <summary>True while tasks are not a Pro feature.</summary>
    public const bool Free = true;

    /// <summary>Whether tasks may be used by a user who is, or is not, on Pro.</summary>
    public static bool Allowed(bool isPro) => Free || isPro;
}
