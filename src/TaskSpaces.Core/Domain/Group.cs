namespace TaskSpaces.Core.Domain;

// A named group of workspaces, drawn together on the bar.
//
// Two kinds of grouping were asked for and this is one record covering both, which was Petre's
// call when the choice was put to him:
//
//   * ANCHORED (#42, "nested workspaces"). AnchorWorkspaceId names a member that is the parent.
//     Its windows are borrowed onto the other members' desktops while you are standing in one of
//     them, and the group wears the anchor's lane colour. The anchor is a member like any other,
//     which is why it is an id rather than a separate field outside the membership list.
//
//   * ANCHORLESS (#84, "visual groups"). AnchorWorkspaceId is null. The group is a name and
//     nothing else: no desktop to switch to, no windows to borrow, no parent icons. Membership is
//     organisational.
//
// Modelling them together rather than side by side is what makes #83's operations (move into a
// group, move out, ungroup) one implementation instead of two, and it is why nothing in the
// manager or the bar has to ask which kind it is except in the two places where the anchor
// genuinely changes behaviour: borrowing windows, and where the name comes from.
//
// Name is stored even for an anchored group. It starts as the anchor's name, and keeping its own
// copy means renaming the anchor workspace does not silently rename the group, and an anchored
// group can lose its anchor (the anchor gets deleted, or moved out) without becoming nameless.
public sealed record Group(Guid Id, string Name, Guid? AnchorWorkspaceId = null)
{
    public bool IsAnchored => AnchorWorkspaceId is not null;

    // The lane colour for the whole group (#90). Petre: "a group has one colour: if any child
    // changes it, the entire group takes that colour", and it is settable "from the parent or any
    // child".
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
