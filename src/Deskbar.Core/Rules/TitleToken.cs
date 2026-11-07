using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Rules;

// Petre: "if a vscode window has a title 'filename - TaskSpaces' you should pay attention to
// the TaskSpaces when assigning", and "so i open vscode, then i load a folder in it, that
// should take it to the correct workspace".
//
// WHY this exists at all: placement is otherwise keyed on RosterIdentity (exe path + args),
// and an app launched WITHOUT arguments has one identity no matter what it has loaded. Three
// VS Code windows started from the Start menu are indistinguishable that way -- which is
// exactly what collapsed all three of Petre's into one workspace. The container name in the
// title is the only per-window signal available, so this extracts it.
//
// ALLOWLIST, never a blocklist. The failure modes are asymmetric: an app we do not recognise
// simply keeps today's behaviour, whereas an app we wrongly recognise silently misplaces
// windows. Same reasoning (and same shape) as RosterIdentity's browser set.
//
// Deliberately EXCLUDED, per Petre:
//   - Browsers. "browser tabs are a bad way to identify which tab to assign it to" -- a tab
//     title is the page, not a container, and it changes on every navigation. The honest fix
//     for browsers is UIA reading the address bar (already parked in the spec as a spike),
//     not a cleverer title heuristic.
//   - Single-window apps (Beeper, WhatsApp, Slack, Teams, Spotify). One window means one
//     identity means one workspace, which placement memory already handles perfectly. Title
//     learning would add risk and no value.
public static class TitleToken
{
    // How an app arranges its title. Not cosmetic -- the container is at OPPOSITE ends for
    // VS Code and for JetBrains IDEs, so one "take the last segment" rule cannot serve both.
    enum Shape
    {
        TrailingContainer, // "file - Container - App Name"      (VS Code, Visual Studio)
        LeadingContainer,  // "Container – file – branch"        (JetBrains, note the EN DASH)
        Bracketed,         // "App Name [Container]"             (Remote Desktop Manager)
    }

    static readonly IReadOnlyDictionary<string, Shape> Apps =
        new Dictionary<string, Shape>(StringComparer.OrdinalIgnoreCase)
        {
            // VS Code and its forks: "index.ts - Corne-Config - Visual Studio Code"
            ["code"] = Shape.TrailingContainer,
            ["cursor"] = Shape.TrailingContainer,
            ["vscodium"] = Shape.TrailingContainer,
            ["windsurf"] = Shape.TrailingContainer,
            // "Program.cs - TaskSpaces - Microsoft Visual Studio"
            ["devenv"] = Shape.TrailingContainer,

            // JetBrains put the PROJECT FIRST and separate with an en dash:
            // "TaskSpaces – Program.cs"
            ["rider64"] = Shape.LeadingContainer,
            ["idea64"] = Shape.LeadingContainer,
            ["pycharm64"] = Shape.LeadingContainer,
            ["webstorm64"] = Shape.LeadingContainer,
            ["goland64"] = Shape.LeadingContainer,
            ["phpstorm64"] = Shape.LeadingContainer,
            ["clion64"] = Shape.LeadingContainer,
            ["datagrip64"] = Shape.LeadingContainer,
            ["studio64"] = Shape.LeadingContainer,

            // Petre's own: "Remote Desktop Manager [server-01 - fhd]" -- the session is
            // bracketed, and note it contains a " - " of its own, which is precisely why the
            // bracket rule has to run BEFORE any splitting.
            ["remotedesktopmanager"] = Shape.Bracketed,
        };

    // Trailing app names to discard before taking the container. Without this, VS Code's
    // container would always come out as "Visual Studio Code".
    static readonly IReadOnlyList<string> AppNameTails =
        ["Visual Studio Code", "Microsoft Visual Studio", "Visual Studio", "Cursor", "VSCodium", "Windsurf",
         // Not a tail for anyone -- RDM puts its name FIRST -- but the bare-title fallback below needs
         // the same list of "this is the app, not a container".
         "Remote Desktop Manager"];

    // Split on the delimiter WITH its surrounding spaces, never on the bare character.
    // "Corne-Config" is one word and splitting on a lone '-' would shred it into
    // "Corne" and "Config" -- the exact folder name Petre needs to match on.
    static readonly Regex Delimiters = new(@" - | – | \| ", RegexOptions.Compiled);

    static readonly Regex Bracketed = new(@"\[(?<container>[^\]]+)\]", RegexOptions.Compiled);

    // The container a window currently has loaded, or None when there is nothing to learn:
    // an app we do not track, or a window with no container yet. The second case matters --
    // a freshly opened VS Code is titled just "Visual Studio Code", and returning None for
    // it is what lets the folder-load a moment later be the thing that places the window.
    // True when this app's container names the TAB you are looking at rather than what the window is.
    // Remote Desktop Manager's dashboard hosts every session in one window and puts the active one in
    // its title, so its container changes as you click around inside a window that has not moved and
    // is not new. Petre: "my rdp keeps jumping across spaces... that jumping should not happen
    // mid-flight", "after minutes of the app / window being open".
    //
    // The editors are deliberately NOT this. A VS Code window that loads another folder really has
    // become a window about something else, and moving it then is a decision already made here.
    public static bool ContainerFollowsTabs(string processName) =>
        Apps.TryGetValue(processName, out var shape) && shape == Shape.Bracketed;

    // `known` is the containers already learned for this app (ContainerHomes), and it only ever
    // widens what a ONE-part title can mean; see the end of this method. Leaving it out keeps the
    // strict reading, which is what learning and Matches want.
    public static Maybe<string> For(string processName, string title, IEnumerable<string>? known = null)
    {
        if (string.IsNullOrWhiteSpace(title) || !Apps.TryGetValue(processName, out var shape))
            return Maybe<string>.None;

        // Brackets first, then the bare title. Remote Desktop Manager writes BOTH shapes and the
        // difference is not cosmetic: its dashboard is "Remote Desktop Manager [Dashboard]", while a
        // session window it opens externally is titled with the session name ALONE -- "i7-petre".
        //
        // Measured, after the log was made to say what the read saw (Petre: "rdp that was opened
        // externally from rdm should be moved to gepha workspace on open", and then "why didn't it?"):
        //
        //   container read RemoteDesktopManager "Remote Desktop Manager [Dashboard]" -> Dashboard
        //   container read RemoteDesktopManager "i7-petre"                           -> none
        //
        // A bracket-only rule therefore reads the one window nobody needs placed and misses every
        // window that is the point of the feature. The whole title is the session name when there are
        // no brackets, so that is what it means.
        //
        // Safe for the OTHER shapes precisely because it is not offered to them: a bare "Visual Studio
        // Code" must stay None so the folder-load a moment later is what places the window, and that
        // path is untouched below.
        // ...with the app's OWN name still meaning nothing, exactly as a bare "Visual Studio Code"
        // does. RDM shows that title between opening the window and connecting the session.
        if (shape == Shape.Bracketed)
            return Bracketed.Match(title) is { Success: true } match
                ? Clean(match.Groups["container"].Value)
                : AppNameTails.Contains(title.Trim(), StringComparer.OrdinalIgnoreCase)
                    ? Maybe<string>.None
                    : Clean(title);

        var raw = Delimiters.Split(title)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToList();
        var segments = raw
            .Where(segment => !AppNameTails.Contains(segment, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Fewer than two segments means the title carries a name but no container: a bare
        // "Visual Studio Code", or "Untitled-1 - Visual Studio Code" with no folder open.
        // Guessing there would learn a FILE name as though it were a project.
        //
        // ...EXCEPT when that one name is a folder this app has already been taught. VS Code titles a
        // window "<folder> - Visual Studio Code" when a folder is open and no editor tab is, which is
        // exactly how a window comes back after a restart if its tabs did not restore. Measured on
        // Petre's machine, 2026-11-07 (Petre: "check why vscode windows didn't go to their
        // corresponding deskbars"):
        //
        //   10:01  read "Service downtime question - hris.ifc.ge - Visual Studio Code" -> hris.ifc.ge
        //          container Code/hris.ifc.ge -> HRIS
        //   13:09  read "hris.ifc.ge - Visual Studio Code" -> none
        //
        // Same window, same folder, a home already learned for it, and five windows left piled on
        // whichever desktop was current. The one-part title is the folder at its plainest, so refusing
        // it made placement depend on which tab VS Code happened to restore.
        //
        // The file-name worry above still holds, and it is why this is a MATCH against known homes and
        // never a read of any one-part title: a new file name is not a known folder, so it still reads
        // as nothing and still teaches nothing. The one wrong answer left is a file named exactly like
        // one of the app's learned folders, opened with no folder loaded. Two more conditions keep it
        // narrow: only the folder-last shape (VS Code and Visual Studio), whose app names are what
        // AppNameTails strips, and only when the app's name WAS in the title, so a bare "hris.ifc.ge"
        // (say, our own folder-name rename left behind by a run that died) is not taken as one.
        if (segments.Count == 1
            && shape == Shape.TrailingContainer
            && raw.Count > segments.Count
            && known is not null
            && known.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            return Clean(segments[0]);

        return segments.Count < 2
            ? Maybe<string>.None
            : Clean(shape == Shape.TrailingContainer ? segments[^1] : segments[0]);
    }

    // A one-character container is noise, not a name worth matching windows on.
    static Maybe<string> Clean(string container) =>
        container.Trim() is { Length: > 1 } cleaned ? cleaned : Maybe<string>.None;

    // Whether this app's title shape is known at all, which is what decides where "Name by folder"
    // is offered (#136). An app not on the list has no container to name a window after, so offering
    // it would be a menu item that silently does nothing.
    public static bool Knows(string processName) => Apps.ContainsKey(processName);

    // The container as a NAME, for a taskbar button. Petre: "folder name, but only the last part."
    //
    // A no-op for the ordinary case, which is why it is safe: VS Code shows the folder name alone, so
    // "TaskSpaces" is already the last part of itself. It earns its keep when the title carries a PATH
    // instead -- a window.title setting that includes the folder path, a JetBrains project shown with
    // its location -- where "C:\repos\bitcoin\dice-to-seed" should name the window "dice-to-seed"
    // rather than filling the taskbar with a path.
    //
    // Both separators, because a WSL or SSH remote in the title uses forward slashes. A trailing
    // separator is dropped first, or a path written "C:\repos\dice\" would come out as nothing.
    public static string LastPart(string container) =>
        container.TrimEnd('\\', '/').Split('\\', '/') is { Length: > 0 } parts && parts[^1].Length > 0
            ? parts[^1]
            : container;

    // Does this window's current content match a token we learned earlier? Case-insensitive
    // containment rather than equality: VS Code shows the folder name verbatim, but other
    // apps decorate it (a trailing "[Administrator]", a branch suffix), and a learned token
    // should survive that.
    public static bool Matches(string processName, string title, string token) =>
        For(processName, title).Map(container => container.Contains(token, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false);
}
