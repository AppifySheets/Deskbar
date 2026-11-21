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

    // The key of the previous workspace's item (BackItem) where an item needs one: the taskbar keeps one button per key
    // (TaskbarJumps), and this item belongs to no pinned workspace. A fixed made-up id, so the button is the same
    // button from one rebuild to the next.
    public static readonly Guid BackId = new("6d1c0b9e-3f2a-4c55-9b8e-2a7f0e4d1b11");

    // THE PREVIOUS WORKSPACE'S ITEM, after the pinned ones on every strip and on the taskbar, when the user has turned
    // it on (AppState.PreviousWorkspaceItem, off by default; FloatingBar.JumpsFor decides, this only builds it). Petre: "let's also have
    // previous workspace available in strip", "and taskbar". "↩ <previous>", going to `back` (the same back as
    // everywhere else), unless an item in `jumps` already goes there: inside a pinned workspace its own item already
    // reads "↩ <previous>", and a pinned previous workspace already has an item of its own. Two items for one place
    // would be one too many. None with nowhere to go back to.
    //
    // Last, after the pinned items, so the pinned items never shift when this one comes and goes.
    public static Maybe<WorkspaceJump> BackItem(IReadOnlyList<WorkspaceJump> jumps, Maybe<Workspace> back) =>
        back
            .Where(to => jumps.All(j => j.Target.HasNoValue || j.Target.Value != to.Id))
            .Map(to => new WorkspaceJump(BackId, $"{BackGlyph} {to.Name}", to.Id, IsBack: true));

    // `current` is the workspace you are on, None on an unnamed desktop (where every item goes in).
    public static IReadOnlyList<WorkspaceJump> Of(IReadOnlyList<Workspace> workspaces, Maybe<Guid> current, Maybe<Workspace> back) =>
        workspaces
            .Where(w => w.Pinned)
            .Select(w => current.HasValue && current.Value == w.Id
                // Inside: back where you came from, or, with nowhere to go (nothing else visited since start), the
                // workspace's own name with no target, so the item stays put rather than vanishing.
                //
                // ...and the same when "back" is another PINNED workspace: its own item already goes there, and two
                // items for one place was one too many (Petre, with a strip showing "↩ messengers" beside
                // "messengers": "only one"). The item keeps its place rather than vanishing, so nothing shifts on the
                // strip or the taskbar.
                ? back.Where(to => !(to.Pinned && to.GroupId is null && workspaces.Any(p => p.Id == to.Id))).Match(
                    to => new WorkspaceJump(w.Id, $"{BackGlyph} {to.Name}", to.Id, IsBack: true),
                    () => new WorkspaceJump(w.Id, w.Name, Maybe<Guid>.None, IsBack: false))
                : new WorkspaceJump(w.Id, w.Name, w.Id, IsBack: false))
            .ToList();
}
