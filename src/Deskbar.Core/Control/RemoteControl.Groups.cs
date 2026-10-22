using CSharpFunctionalExtensions;

namespace Deskbar.Core.Control;

// The group menu and the group header's menu. See RemoteControl.cs for the shared parts.
public sealed partial class RemoteControl
{
    // A group is born holding the workspace it was made from, exactly as "New group…" on a row makes one.
    ControlReply GroupCreate(string name, string firstWorkspace) =>
        WorkspaceNamed(firstWorkspace).Bind(w => manager.CreateGroup(name, w.Id)).Match(
            group => Success(DescribeGroup(group.Id)),
            ControlReply.Fail);

    // Joins at the bottom of the group, which is where the bar's "Move into group" puts it.
    ControlReply GroupJoin(string workspace, string group) =>
        WorkspaceNamed(workspace).Bind(w => GroupNamed(group).Bind(g => manager.MoveIntoGroup(w.Id, g.Id)).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    ControlReply GroupLeave(string workspace) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.LeaveGroup(w.Id), () => Describe(w.Id)),
            ControlReply.Fail);

    ControlReply GroupRename(string group, string name) =>
        GroupNamed(group).Match(
            g => Reply(manager.RenameGroup(g.Id, name), () => DescribeGroup(g.Id)),
            ControlReply.Fail);

    ControlReply GroupColour(string group, string colour) =>
        GroupNamed(group).Bind(g => ParseColour(colour).Bind(c => manager.SetGroupColor(g.Id, c)).Map(() => g.Id))
            .Match(id => Success(DescribeGroup(id)), ControlReply.Fail);

    // The group header's Fold group / Unfold group. Each member's own fold is left as it was.
    ControlReply GroupFold(string group, bool folded) =>
        GroupNamed(group).Match(
            g => Reply(manager.SetGroupFolded(g.Id, folded), () => DescribeGroup(g.Id)),
            ControlReply.Fail);

    // The group header's four moves, among the bar's top-level rows, plus an exact position counted the
    // same way (a group is one row of the bar however many members it has). "end" passes the workspace
    // count, which is always past the last top-level row, and the manager clamps it.
    ControlReply GroupReorder(string group, string where) =>
        GroupNamed(group).Bind(g => (where.ToLowerInvariant() switch
            {
                "up" => manager.MoveGroup(g.Id, -1),
                "down" => manager.MoveGroup(g.Id, +1),
                "top" => manager.MoveGroupTo(g.Id, 0),
                "end" or "bottom" => manager.MoveGroupTo(g.Id, manager.State.Workspaces.Count),
                _ => ControlArguments.Position(where)
                    .MapError(_ => $"group-reorder takes up, down, top, end or a position from 1, not '{where}'.")
                    .Bind(position => manager.MoveGroupTo(g.Id, position - 1)),
            }).Map(() => g.Id))
            .Match(id => Success(DescribeGroup(id)), ControlReply.Fail);

    // The members keep their desktops, windows and places in the list; the reply names them so the
    // caller can see who was freed.
    ControlReply Ungroup(string group) =>
        GroupNamed(group).Match(
            g =>
            {
                var members = manager.State.MembersOf(g.Id).Select(m => m.Name).ToList();
                return Reply(manager.Ungroup(g.Id), () => new { ungrouped = g.Name, members });
            },
            ControlReply.Fail);
}
