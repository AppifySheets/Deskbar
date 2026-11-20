using System.Text.Json.Serialization;

namespace Deskbar.Core.Domain;

// A named group of workspaces, drawn together on the bar.
//
// One kind of group (#84, "visual groups"): a name and nothing else. No desktop to switch to, no
// windows to borrow, no parent icons. Membership is organisational, the members are the workspaces
// whose GroupId is this group's Id, and they are drawn in list order inside one box under a header
// carrying the name. Because it is only a name, #83's operations (move into a group, move out,
// ungroup) are one implementation.
//
// There used to be a second, ANCHORED kind (#42, "nested workspaces"), whose anchor workspace lent
// its windows to the other members. It was removed (see
// docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md); AnchorWorkspaceId below survives only
// so an old state.json can still be read.
public sealed record Group(Guid Id, string Name)
{
    // LEGACY, and on its way out: child workspaces are being removed (see
    // docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "Migration"). It is no longer part of
    // the record's shape, only an init property, because an old state.json still carries it and
    // AppState.Migrated reads it once to turn an anchored group into a plain named group (anchor
    // moved to the front, colour kept) before clearing it.
    //
    // Null in everything this build writes: WhenWritingNull keeps the key out of new files. It is
    // still READ from old ones, which is the whole reason it has not been deleted yet.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AnchorWorkspaceId { get; init; }

    // The lane colour for the whole group (#90). Petre: "a group has one colour: if any child
    // changes it, the entire group takes that colour", and it is settable from any member.
    //
    // It lives HERE rather than being copied onto each member, which is what makes both halves of
    // that work without extra bookkeeping. Joining takes the group's colour and leaving gives a
    // workspace its own Workspace.Color back, because the group's colour was never written onto it.
    // It also fixes #92, where a group took the colour of an incoming member: the group's colour is
    // the group's, and nothing a member carries can overwrite it.
    //
    // Same three states as Workspace.Color: null follows the group's position in the list,
    // WorkspacePalette.None opts out of a lane colour, anything else is a hex.
    public string? Color { get; init; }

    // The whole group drawn as ONE line: its name, then each member's name as a click target, then how
    // many windows the members hold between them. Petre: "i should be able to minimize the entire group."
    //
    // Separate from each member's own Workspace.Folded, and deliberately so: unfolding the group puts every
    // member back exactly as it was, folded or not, instead of the group's fold overwriting what each
    // member said about itself. Like a member's fold, it is a wish the bar honours except for the workspace
    // you are on, whose row is still drawn under the group's line.
    //
    // An init property, defaulting to false, so a state.json from before it loads with every group open.
    public bool Folded { get; init; }
}
