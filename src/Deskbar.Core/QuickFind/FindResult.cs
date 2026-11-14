using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.QuickFind;

// One row quick find can show (docs/superpowers/specs/2026-11-04-quick-find-design.md).
//
// Window and Tab are things already open; Enter goes to them. Remembered and StartMenu are programs that are
// not running; Enter starts them where the row says (pull request C of the plan). The order of this enum is
// NOT the ranking: FindRanking decides that, and keeps windows and tabs in one group.
//
// NewWindow is a program that IS running, offered once more: Enter opens another window of it where Petre is,
// as a middle click on its taskbar button does. Petre, with sixteen VS Code windows open: "i want just a new
// code window, i should be able to do that".
public enum FindKind { Window, Tab, Remembered, StartMenu, NewWindow }

// Where a result is, as the row says it on the right. Petre: "each window should say where it is". The
// workspace is carried as an id as well as a label so the row can be drawn in that workspace's lane colour and
// ranking can prefer the workspace used most recently. Null for a window on an unnamed desktop or pinned to
// all of them, whose labels say exactly that.
public sealed record FindPlace(Guid? WorkspaceId, string Label);

// What starting a not-running result does: the program and its arguments as ProgramStarter takes them, and the
// exe name its window will appear under, so the start can be held to the promised workspace
// (WorkspaceManager.ExpectWindow). Null ExpectedProcess when nothing about the window is known in advance: a
// packaged app started through the Start menu opens under ApplicationFrameHost, not under anything it was
// started as (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
public sealed record LaunchTarget(ProgramLaunch Launch, string? ExpectedProcess);

// Key is the result's identity across refreshes, so the highlight stays on the same result while tab readings
// arrive in the background (the spec's anti-goal: results must not reshuffle under the cursor):
//   w:<hwnd>                 a window
//   t:<hwnd>:<index>:<title> a tab
//   r:<roster identity>      a remembered program
//   s:<parsing name>         a Start menu app
//
// SearchText is what the query is matched against: the title plus where it is plus Detail, so "acc inv" finds
// invoices on the Accounting workspace. Detail is the line a row shows under or beside its title: the browser
// window a tab is in, or "not running".
//
// Two facts ranking needs and nothing else reads:
//   OpenWindows  on a NewWindow row, how many windows of that program are open. With several there is no
//                single window to go to, so naming the program puts this row first (FindRanking).
//   TabsStandIn  on a browser window whose tabs are listed too: the window is shown only when a search finds
//                none of its tabs, so a tab and the window holding it never both appear.
public sealed record FindResult(
    string Key,
    FindKind Kind,
    string Title,
    FindPlace Place,
    string SearchText,
    WindowHandle? Window = null,
    int? TabIndex = null,
    string? Detail = null,
    string? IconPath = null,
    LaunchTarget? Launch = null,
    int OpenWindows = 0,
    bool TabsStandIn = false)
{
    public bool IsOpen => Kind is FindKind.Window or FindKind.Tab;
}
