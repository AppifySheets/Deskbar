using System.IO;
using System.Net.Http;
using System.Windows;
using CSharpFunctionalExtensions;
using Deskbar.Core.Reporting;
using Deskbar.Windows.Packaging;
using Deskbar.Windows.Processes;

namespace Deskbar.App;

// The real services behind the report window (ReportWindow, issue #331): the relay, the fallback folder, Explorer
// and Notepad. Kept out of the window so the window's tests run without any of them.
//
// THE NETWORK: this is the app's second piece of network traffic, after the update check (UpdateService), and it
// only ever runs when the user presses Send. README's Updates section says so. The relay is report.deskbar.app, a
// Cloudflare Worker kept in relay/ in this repo, which holds the GitHub token; the app holds no secret.
static class ReportService
{
    static readonly Uri Relay = new("https://report.deskbar.app/");

    // One client for the process, as UpdateService does. The relay refuses a request whose User-Agent does not start
    // with "Deskbar/" (relay/src/index.js). Two minutes per request: a 5 MB screenshot on a slow line takes a while,
    // and the window shows progress meanwhile.
    static readonly HttpClient Client = Build();

    static HttpClient Build()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add("User-Agent", $"Deskbar/{ReportVersion.Of(UpdateService.RunningVersion)}");
        return client;
    }

    // %APPDATA%\Deskbar\reports: beside dumps, where a report that could not be sent is kept (ReportArchive).
    static string ReportsFolder => Path.Combine(AppPaths.NewStateDir, "reports");

    public static ReportServices Live() => new(
        Send: new ReportClient(Client, Relay).SendAsync,
        Archive: report => ReportArchive.Save(ReportsFolder, report, DateTime.Now),
        Forget: ReportArchive.Forget,
        // Through DetachedLauncher, so under the Store copy Explorer is not part of Deskbar's package (store packaging
        // design, docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md). The folder goes as one quoted
        // argument, the way DebugDump and TaskbarPins hand Explorer a path.
        OpenFolder: folder => DetachedLauncher.StartVerbatim("explorer.exe", $"\"{folder}\"", null)
            .TapError(error => Warn($"The report is in {folder}, but Explorer could not be opened: {error}")),
        ViewDump: text => DebugDump.SaveText(text).Bind(DebugDump.Open).TapError(Warn),
        AppVersion: ReportVersion.Of(UpdateService.RunningVersion),
        WindowsBuild: ReportVersion.Windows(Environment.OSVersion.Version),
        // Which build the report came from, the Store copy or the direct download (store packaging design).
        Store: PackageIdentity.IsPackaged,
        // Whether this copy owns the Deskbar Pro add-on (Entitlement, the cached verdict, so opening the window never
        // waits on the Store). Pro comes with priority support: the relay labels such a report "priority".
        Pro: Entitlement.Current.IsPro);

    static void Warn(string message) => MessageBox.Show(message, "Deskbar", MessageBoxButton.OK, MessageBoxImage.Warning);
}
