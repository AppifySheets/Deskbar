using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The one place that decides whether tiling acts on a workspace. Three things must all hold: the
// experiment is on (Petre: "disabled by default, until i have tested it well"), the workspace is
// marked tiled, and the build is Pro. Callers pass the facts in, so this stays pure and testable;
// `untile` is deliberately not gated and always works.
public static class TilingGate
{
    public static bool Acts(Workspace workspace, bool isPro, bool experimentOn) =>
        experimentOn && workspace.Tiled && isPro;
}
