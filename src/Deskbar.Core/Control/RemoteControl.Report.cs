using CSharpFunctionalExtensions;
using Deskbar.Core.Reporting;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Control;

// `report <text>|-`: the remote twin of "Report a problem…" (task c28ed674, "let the manager session submit a bug report
// to deskbar"). The manager session has no window and no hands, and the user may be talking to it from the phone, so it
// cannot fill in the report window and press Send. With this it files the same report the window files: the text, a
// debug dump of this moment, and the build, to the relay at report.deskbar.app, which files it in the private
// Deskbar-reports repo.
//
// WHY IT OPENS THE WINDOW AND ANSWERS AT ONCE, instead of sending and answering the issue number: the control pipe is
// ONE instance serving one request at a time (ControlPipeServer), and every Claude hook on the machine goes through it
// with half a second to connect. A send takes seconds (a dump to upload, a slow line), and holding the pipe for that
// long would drop hook calls from every session. So the app opens the report window already filled in and sending
// (ReportWindow.SendNow), and the window shows what the user would see after pressing Send themselves: the progress,
// the report's reference once it is filed, or the saved folder and Try again when it fails. The reply carries the
// report's id, which is also the name the relay files it under.
//
// The text is marked with who sent it from the command line, so whoever reads the report knows the words may be an
// agent's summary rather than the user's own: the manager (told by the caller's folder, as `task` tells it), or
// another caller of deskbar.cmd.
public sealed partial class RemoteControl
{
    /// <summary>What a report sent by the manager session carries at its end, so the reader knows who wrote it.</summary>
    public const string ManagerReportNote = "Sent by the Deskbar manager session at the user's request (deskbar.cmd report).";

    /// <summary>What a report sent by any other caller of the command carries at its end.</summary>
    public const string CommandReportNote = "Sent from the command line (deskbar.cmd report).";

    ControlReply Report(string[] rest)
    {
        // The client appends the caller's folder last (ControlArguments.WithCallerFolder), as it does for `task`.
        var (words, from) = SplitFrom(rest);
        if (words is not [var text] || text.Trim().Length == 0)
            return ControlReply.Fail($"Say what happened.\n{ControlUsage.LineFor("report")}");
        if (fileReport is null) return ControlReply.Fail("A problem report cannot be sent from here.");

        var byManager = ManagerSession.IsManager(Folder(from), taskSessions?.ManagerFolder ?? "", LongPath);
        var signed = $"{text.Trim()}\n\n{(byManager ? ManagerReportNote : CommandReportNote)}";
        // Refused here rather than by the relay, which would refuse it only after the dump had gone up.
        if (signed.Length > ReportLimits.TextChars)
            return ControlReply.Fail($"The report is {signed.Length:N0} characters with its note; the limit is {ReportLimits.TextChars:N0}.");

        return fileReport(signed).Match(
            id => Success(new
            {
                report = id,
                sending = true,
                // Said in the reply because the caller cannot see the window: where the outcome shows.
                outcome = "The Report a problem window on the user's screen shows the send and the report's reference.",
            }),
            ControlReply.Fail);
    }
}
