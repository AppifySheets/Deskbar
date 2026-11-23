using System.IO;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Claude;

// THE WINDOWS TERMINAL PROFILE "Deskbar Claude", installed as a fragment. Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md.
//
// Windows Terminal reads every `*.json` under `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\<app>\` as extra
// profiles and colour schemes, without touching the user's settings.json, so Deskbar can add its profile and take it
// away again by creating and deleting one folder of its own. ClaudeStart.AttachWindow opens a window with `-p
// "Deskbar Claude"`, which is why the name is a constant both sides share.
//
// THE LOOK is "Dark Modern", which Petre picked from three trial profiles (Fragments\DeskbarClaudeTry, written by
// hand while choosing). The values are copied from that trial fragment: VS Code's Dark Modern colours with Claude's
// orange (#D97757) as the cursor, JetBrains Mono 10 pt, a bar cursor, no scrollbar. suppressApplicationTitle keeps the
// window titled with the task's name (--title) instead of whatever `claude` writes into the title.
//
// The font is the one thing that depends on the machine: JetBrains Mono when it is installed, else Cascadia Mono
// (ships with Windows Terminal), so the profile never names a face the terminal would silently substitute.
//
// Never throws: a locked folder or a full disk is an expected failure and comes back as a Result for the caller to
// show, since a terminal profile that cannot be written must not break the task that wanted it.
public static class TerminalFragment
{
    public const string ProfileName = "Deskbar Claude";
    public const string SchemeName = "Deskbar Dark Modern";

    const string PreferredFont = "JetBrains Mono";
    const string FallbackFont = "Cascadia Mono";

    static string FragmentsOf(string localAppData) => Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments");
    static string DeskbarFolder(string localAppData) => Path.Combine(FragmentsOf(localAppData), "Deskbar");
    static string TrialFolder(string localAppData) => Path.Combine(FragmentsOf(localAppData), "DeskbarClaudeTry");

    /// <summary>The file Windows Terminal reads, for callers that want to show where the profile lives.</summary>
    public static string FragmentPath(string localAppData) => Path.Combine(DeskbarFolder(localAppData), "deskbar-claude.json");

    /// <summary>
    /// Makes the fragment match <paramref name="on"/>. On: writes the profile and scheme (durably, like state.json) and
    /// removes the hand-made trial fragment, whose profiles would sit beside ours as clutter. Off: removes Deskbar's
    /// folder and the trial one, and leaves every other fragment alone.
    /// <paramref name="fontInstalled"/> answers whether a font family is installed.
    /// </summary>
    //
    // The trial folder goes LAST and in its own try (final review, item 11): it is only clutter, so a file in it that
    // Windows Terminal or an editor holds open must never stop Deskbar's own profile from being written or removed. Its
    // failure is not reported either; the next Sync (every start, every toggle) tries again.
    public static Result Sync(bool on, string localAppData, Func<string, bool> fontInstalled)
    {
        var synced = Result.Try(() =>
        {
            if (!on)
            {
                DeleteFolder(DeskbarFolder(localAppData));
                return;
            }
            Directory.CreateDirectory(DeskbarFolder(localAppData));
            DurableFile.Write(FragmentPath(localAppData), Content(fontInstalled(PreferredFont) ? PreferredFont : FallbackFont));
        }, e => $"Could not update the Windows Terminal profile: {e.Message}");
        // Tried whatever happened above, and its own failure dropped (see above).
        Result.Try(() => DeleteFolder(TrialFolder(localAppData)));
        return synced;
    }

    static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // The fragment's JSON. An anonymous object keeps the property order as written and lets System.Text.Json do the
    // escaping, so a font name or path can never break the file.
    static string Content(string fontFace) =>
        JsonSerializer.Serialize(new
        {
            profiles = new[]
            {
                new
                {
                    name = ProfileName,
                    commandline = "claude",
                    hidden = false,
                    font = new { face = fontFace, size = 10, weight = "normal" },
                    lineHeight = 1.15,
                    padding = "14, 10, 14, 10",
                    colorScheme = SchemeName,
                    cursorShape = "bar",
                    cursorColor = "#D97757",
                    antialiasingMode = "cleartype",
                    scrollbarState = "hidden",
                    tabTitle = "Claude",
                    suppressApplicationTitle = true,
                },
            },
            schemes = new[]
            {
                new Dictionary<string, string>
                {
                    ["name"] = SchemeName,
                    ["background"] = "#1F1F1F", ["foreground"] = "#CCCCCC", ["cursorColor"] = "#D97757", ["selectionBackground"] = "#264F78",
                    ["black"] = "#1F1F1F", ["red"] = "#F14C4C", ["green"] = "#23D18B", ["yellow"] = "#F5F543",
                    ["blue"] = "#3B8EEA", ["purple"] = "#D670D6", ["cyan"] = "#29B8DB", ["white"] = "#CCCCCC",
                    ["brightBlack"] = "#6E7681", ["brightRed"] = "#F14C4C", ["brightGreen"] = "#23D18B", ["brightYellow"] = "#F5F543",
                    ["brightBlue"] = "#3B8EEA", ["brightPurple"] = "#D670D6", ["brightCyan"] = "#29B8DB", ["brightWhite"] = "#FFFFFF",
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true });
}
