using CSharpFunctionalExtensions;
using Microsoft.Win32;

namespace Deskbar.Windows.Control;

// The default browser's exe, for `open` (RemoteControl.Open), which starts a NEW window of it in a background
// workspace. A plain shell open of the link cannot do that: it hands the link to whatever window the browser
// picks, on the desktop in front.
//
// Read the way Windows itself resolves an https link: the user's choice for the https scheme names a ProgId, and
// that ProgId's open command names the exe ("C:\...\msedge.exe" --single-argument %1). Only the exe is kept; `open`
// passes its own arguments (--new-window and the link), which Chromium browsers and Firefox both accept.
//
// Asked on every `open` rather than cached, because the user can change the default browser while Deskbar runs,
// and two registry reads cost nothing next to starting a browser.
public static class DefaultBrowser
{
    const string Choice = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

    public static Maybe<string> Exe()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(Choice);
            if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0) return Maybe<string>.None;

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            return command?.GetValue(null) is string line ? ExeOf(line) : Maybe<string>.None;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Maybe<string>.None;
        }
    }

    // The program part of an open command: the quoted path when it is quoted, else everything up to the first
    // space. Only an exe that exists is an answer, so a stale association falls back to opening the link as usual.
    public static Maybe<string> ExeOf(string commandLine)
    {
        var line = commandLine.Trim();
        var exe = line.StartsWith('"')
            ? line[1..].Split('"')[0]
            : line.Split(' ')[0];
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : Maybe<string>.None;
    }
}
