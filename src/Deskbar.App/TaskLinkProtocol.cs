using System.Reflection;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Win32;

namespace Deskbar.App;

// THE deskbar: URL PROTOCOL, so a clicked task link starts this exe with the link (task 5016d533; TaskLink has both
// forms of the link, TaskLinkCommandLine what the exe does with one).
//
// The direct build registers it per user, under HKCU\Software\Classes\deskbar, the documented shape of a URL protocol
// ("Registering an Application to a URI Scheme" on Microsoft Learn): a default value naming it, an empty "URL Protocol"
// value that makes it a protocol rather than a file type, an icon, and shell\open\command running the exe with the
// link as "%1". Per user needs no elevation and touches nothing another account sees.
//
// Kept pointing at the running exe on every start, the way the Run value and the Start menu shortcut are
// (StartupRegistration.ReassertIfEnabled, StartMenuShortcut): an update is a NEW FILE beside the old one, and a command
// written once would go on starting the old version. The same guards too: only Deskbar itself writes it, never a test
// host, and only a PUBLISHED build, so a development copy never takes links away from the installed one.
//
// The Store build declares the protocol in its package manifest instead (windows.protocol), and Windows removes that
// with the package. RunsAsPublished is false under the package (it is not a single-file publish), so this never writes
// there; PackageIdentity is checked as well, because a key written from inside the package would be virtualised away.
//
// Written only when something differs, so an ordinary start reads four values and writes nothing.
public static class TaskLinkProtocol
{
    const string ClassesKey = @"Software\Classes\" + TaskLink.Scheme;

    // What the key should hold for this exe: (subkey, value name, value), "" naming a key's default value.
    internal static IReadOnlyList<(string Key, string Name, string Value)> EntriesFor(string exe) =>
    [
        ("", "", "URL:Deskbar task link"),
        ("", "URL Protocol", ""),
        ("DefaultIcon", "", $"\"{exe}\",0"),
        (@"shell\open\command", "", $"\"{exe}\" \"%1\""),
    ];

    // Called on every start, beside the Start menu shortcut. Success when there was nothing to do (a development build,
    // a test host, the Store copy, a key already right); a failure only when writing went wrong, which costs the links
    // and nothing else, so the caller only traces it.
    public static Result Ensure()
    {
        if (!StartupRegistration.IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name) || !StartupRegistration.RunsAsPublished)
            return Result.Success();
        // Store packaging design (docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md): the package
        // declares the protocol in its manifest, so the packaged copy writes nothing here.
        if (Windows.Packaging.PackageIdentity.IsPackaged) return Result.Success();
        if (Environment.ProcessPath is not { } exe) return Result.Failure("the running exe has no path");

        return Result.Try(() =>
            EntriesFor(exe)
                .Where(entry => !IsWritten(entry))
                .ToList()
                .ForEach(entry =>
                {
                    using var key = Registry.CurrentUser.CreateSubKey(KeyPath(entry.Key));
                    key.SetValue(entry.Name, entry.Value);
                }),
            error => $"could not register the {TaskLink.Scheme}: protocol: {error.Message}");
    }

    // Whether the direct build's key is there: the Store copy's takeover asks, and deletes it (PackagedTakeover).
    internal static bool DirectKeyExists
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassesKey);
            return key is not null;
        }
    }

    // The takeover's delete. Under the package an HKCU delete would only hide the key from the package itself, so it
    // goes to reg.exe outside the package, as StartupRegistration.DeleteValue does for the Run value.
    internal static Result DeleteDirectKey() =>
        Windows.Packaging.PackageIdentity.IsPackaged
            ? Windows.Processes.DetachedLauncher.RunOutsidePackage(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
                ["delete", $@"HKCU\{ClassesKey}", "/f"], TimeSpan.FromSeconds(10))
            : Result.Try(() => Registry.CurrentUser.DeleteSubKeyTree(ClassesKey, throwOnMissingSubKey: false), error => error.Message);

    static bool IsWritten((string Key, string Name, string Value) entry)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(entry.Key));
        return Equals(key?.GetValue(entry.Name), entry.Value);
    }

    static string KeyPath(string subkey) => subkey.Length == 0 ? ClassesKey : $@"{ClassesKey}\{subkey}";
}
