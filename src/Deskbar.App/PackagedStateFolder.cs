using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Packaging;
using Deskbar.Windows.Processes;

namespace Deskbar.App;

// The Store copy's state folder, %APPDATA%\Deskbar, made a REAL folder before anything writes into it.
// Store packaging design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Why: Microsoft refused the unvirtualizedResources capability at certification (2026-11-02), so the
// manifest no longer excludes this folder from write virtualisation. Measured with a throwaway probe package
// the same day (docs/superpowers/notes/2026-10-24-store-package-spike.md, "Without unvirtualizedResources"):
// a packaged process that creates a NEW top-level AppData folder gets the package's private copy of it in
// LocalCache, which nothing outside the package can see; but everything it writes into a folder that already
// exists outside (new files, subfolders, File.Replace, deletes) lands in the real folder. So on a fresh
// Store install the folder is created once from OUTSIDE the package, by cmd's mkdir through
// DetachedLauncher.RunOutsidePackage, and from then on Deskbar's state, its deskbar.cmd shim, its dumps and
// trace.on all live where the direct build, Claude Code and Explorer look for them.
//
// When the folder already exists (the direct build ran here, or this is not the first start) nothing
// happens. When the mkdir fails the start goes on: the folder is then the package's private copy, which
// works for the app itself and only hides the files from everything else. Traced either way.
static class PackagedStateFolder
{
    public static void EnsureReal(string stateDir, Action<string> trace)
    {
        if (!PackageIdentity.IsPackaged || Directory.Exists(stateDir)) return;
        DetachedLauncher.RunOutsidePackage(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["/d", "/c", "mkdir", stateDir], TimeSpan.FromSeconds(10))
            .Match(
                () => trace($"packaged start: created {stateDir} outside the package"),
                error => trace($"packaged start: could not create {stateDir} outside the package ({error}), it will be the package's private copy"));
    }
}
