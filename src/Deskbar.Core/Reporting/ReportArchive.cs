using System.Text;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Reporting;

// Where a report goes when it could not be sent: one dated folder under %APPDATA%\Deskbar\reports, holding
// report.txt (what the user wrote, the contact line, the build) and every attachment as the file it is.
// The window names the folder, so "nothing is lost" is something the user can check, and the folder can be
// attached to an email or a GitHub issue by hand.
//
// The folder is deleted again when a later Try again gets the same report through (Forget), so the reports
// folder only ever holds reports that never arrived.
public static class ReportArchive
{
    public static Result<string> Save(string root, ProblemReport report, DateTime now) =>
        Result.Try(() =>
        {
            // The time to the second, and the report id's start in case two reports fail within one second.
            var folder = Path.Combine(root, $"{now:yyyy-MM-dd HHmmss} {report.Id[..8]}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), Describe(report), Encoding.UTF8);
            report.Attachments.ToList().ForEach(file => File.WriteAllBytes(Path.Combine(folder, file.Name), file.Bytes));
            return folder;
        }, e => $"The report could not be saved either: {e.Message}");

    /// <summary>Removes a saved report once it has been sent after all. Quietly, since it is only tidying.</summary>
    public static void Forget(string folder) =>
        Result.Try(() => { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); });

    static string Describe(ProblemReport report) =>
        string.Join(Environment.NewLine,
        [
            "Deskbar problem report (not sent)",
            $"Deskbar {report.AppVersion}{(report.Store ? " (Store build)" : "")}{(report.Pro ? ", Deskbar Pro" : "")} on Windows {report.WindowsBuild}",
            $"Contact: {(report.Contact.Length > 0 ? report.Contact : "none given")}",
            $"Report id: {report.Id}",
            $"Files: {(report.Attachments.Count > 0 ? string.Join(", ", report.Attachments.Select(a => a.Name)) : "none")}",
            "",
            "What happened:",
            report.Text,
            "",
        ]);
}
