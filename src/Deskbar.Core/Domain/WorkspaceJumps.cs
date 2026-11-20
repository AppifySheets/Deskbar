using CSharpFunctionalExtensions;

namespace Deskbar.Core.Domain;

// One pinned workspace's item, as the strips, the bar's header and the taskbar button all draw it
// (docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md).
//
//   PinnedId  the pinned workspace the item belongs to; the key a redraw keeps an item by, so a taskbar button
//             is retitled rather than recreated and never jumps along the taskbar
//   Label     what it reads: the workspace's name, or "↩ <where you came from>" while you are in it
//   Target    the workspace a click switches to; None when there is nowhere to go (inside, nothing visited before)
//   IsBack    the flipped form, so a surface can tell "go back" from "go in" without parsing the label
public sealed record WorkspaceJump(Guid PinnedId, string Label, Maybe<Guid> Target, bool IsBack);

// What each pinned workspace's item says and where it goes. Petre: "i need to jump in and out of the messengers".
//
// ONE ITEM THAT FLIPS, rather than a pinned item plus a separate back item (Petre picked "A" of three): going in and
// coming back are the same spot under the pointer, which is the habit the loop needs. Outside the workspace the
// item goes in; inside it the same item goes back.
//
// Pure, with the facts handed in, so every rule is a unit test. `back` is WorkspaceManager.ByRecentUse().Back, the
// target the bar's ↩ button and a Win+Ctrl+Tab tap already use, so the three never disagree about "back", and it is
// right however you arrived (the bar, the switcher, Win+Ctrl+arrows, Task View all feed that MRU).
public static class WorkspaceJumps
{
    // The bar's own back button's glyph (FloatingBar.xaml, BackButton), so "back" looks the same everywhere.
    public const string BackGlyph = "↩";

    // `current` is the workspace you are on, None on an unnamed desktop (where every item goes in).
    public static IReadOnlyList<WorkspaceJump> Of(IReadOnlyList<Workspace> workspaces, Maybe<Guid> current, Maybe<Workspace> back) =>
        workspaces
            .Where(w => w.Pinned)
            .Select(w => current.HasValue && current.Value == w.Id
                // Inside: back where you came from, or, with nowhere to go (nothing else visited since start), the
                // workspace's own name with no target, so the item stays put rather than vanishing.
                ? back.Match(
                    to => new WorkspaceJump(w.Id, $"{BackGlyph} {to.Name}", to.Id, IsBack: true),
                    () => new WorkspaceJump(w.Id, w.Name, Maybe<Guid>.None, IsBack: false))
                : new WorkspaceJump(w.Id, w.Name, w.Id, IsBack: false))
            .ToList();
}
