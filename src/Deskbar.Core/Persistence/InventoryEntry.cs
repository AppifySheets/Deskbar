namespace Deskbar.Core.Persistence;

// What we remember about a window so a later one of the same app can be recognised: enough to relaunch
// the app (path + original command line) and to show the user what would come back.
public sealed record InventoryEntry(string ProcessPath, string? CommandLine, string Title);

// LEGACY: one window a child workspace pinned on its parent's behalf, and the desktop it lives on when it
// is not being borrowed (#42). Only read, to repair a crash from a version that still had child workspaces.
// See AppState.InheritedPins for why raw hwnds are the right key here and why this still exists.
public sealed record InheritedPin(long Window, Guid HomeDesktop);
