namespace Deskbar.Core.Rehydration;

// What an exe path or an AppUserModelID says about the Store package it belongs to.
//
// A packaged program runs from a folder named after one VERSION of its package:
//   C:\Program Files\WindowsApps\MSTeams_26288.2004.5269.3420_x64__8wekyb3d8bbwe\ms-teams.exe
// so the path stops existing at the next update, and nothing should be started by it. What stays the same is the
// package FAMILY, "MSTeams_8wekyb3d8bbwe" (the name before the first '_' plus the publisher id after "__"), and the
// AppUserModelID starts with that family: "MSTeams_8wekyb3d8bbwe!MSTeams". Quick find (LaunchCatalog) and bookmarks
// both key packaged programs on the family for that reason.
public static class PackagedPaths
{
    // Whether the exe lives in a package's install folder.
    public static bool IsPackaged(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    // "...\WindowsApps\Microsoft.Windows.Photos_2025.11010.4001.0_x64__8wekyb3d8bbwe\Photos.exe" becomes
    // "Microsoft.Windows.Photos_8wekyb3d8bbwe". Null when the path has no package folder in that shape.
    public static string? FamilyOf(string path)
    {
        var folder = path.Split('\\').SkipWhile(p => !p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (folder is null) return null;
        var nameEnd = folder.IndexOf('_');
        var publisher = folder.LastIndexOf("__", StringComparison.Ordinal);
        return nameEnd < 0 || publisher < 0 ? null : $"{folder[..nameEnd]}_{folder[(publisher + 2)..]}";
    }

    // "MSTeams_8wekyb3d8bbwe!MSTeams" becomes "MSTeams_8wekyb3d8bbwe". Null for an id with no '!', which is no
    // packaged app's id ("MSEdge", "Chrome").
    public static string? FamilyOfAppId(string appId) =>
        appId.IndexOf('!') is var bang and > 0 ? appId[..bang] : null;
}
