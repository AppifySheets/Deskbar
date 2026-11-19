using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The one place that decides whether tiling acts on a workspace. Two things must both hold: the
// workspace is marked tiled, and the copy may tile (Allowed). Callers pass the facts in, so this stays
// pure and testable; `untile` is deliberately not gated and always works.
//
// There used to be a third, Manage's "Tiling (experimental)" box (AppState.ExperimentalTiling), off by
// default. Petre, 2026-11-19: "enable tiling in the settings and remove it as an option; it's enough to
// have a button at the top". So tiling is always available, and the bar's tile button (FloatingBar.Tiling.cs)
// is the one way a workspace turns it on. A state.json that still carries the old key loads fine: unknown
// keys are ignored.
public static class TilingGate
{
    // Tiling is free for now, in every copy: the GitHub exe as well as the Store's Deskbar Pro (Petre,
    // 2026-11-18: "Make it a free feature for now"). The Pro plumbing stays where it is, the entitlement,
    // the "Tile windows (Deskbar Pro)" menu item that offers the Store, the `tile` refusal, so putting
    // tiling back behind Pro is this one line turned to false. Every caller that used to ask "is this
    // Pro?" for tiling asks Allowed instead (App hands it to the Tiler, the control pipe and the bar).
    public static bool Free => true;

    // Whether this copy may tile, given whether it is Deskbar Pro: always while tiling is Free.
    public static bool Allowed(bool isPro) => Free || isPro;

    public static bool Acts(Workspace workspace, bool isPro) => workspace.Tiled && isPro;
}
