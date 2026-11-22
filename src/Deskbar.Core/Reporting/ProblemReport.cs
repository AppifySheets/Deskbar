using System.Text;

namespace Deskbar.Core.Reporting;

// A problem report as "Report a problem…" sends it (issue #331): what the user wrote, how to reach them,
// which build and Windows it came from, and the files that go with it (screenshots and the debug dump).
//
// It goes to the relay at report.deskbar.app (relay/ in this repo), which files it as an issue in the
// PRIVATE repo AppifySheets/Deskbar-reports. The shapes and limits here mirror relay/src/report.js; a
// change to one side has to be made on the other, or the relay refuses what the app sends.

/// <summary>One file sent with a report: a screenshot (shot-N.png or shot-N.jpg) or the dump (dump.txt).</summary>
public sealed record ReportAttachment(string Name, byte[] Bytes);

/// <summary>
/// The whole report. <see cref="Id"/> is made once per report window, so pressing Try again after a failed
/// send resends under the same id and the relay recognises the files that already went up. <see cref="Pro"/> is
/// whether this copy owns the Deskbar Pro add-on: Pro comes with priority support, so the relay labels such a
/// report "priority".
/// </summary>
public sealed record ProblemReport(
    string Id,
    string Text,
    string Contact,
    string AppVersion,
    string WindowsBuild,
    bool Store,
    bool Pro,
    IReadOnlyList<ReportAttachment> Attachments)
{
    /// <summary>A GUID in "N" format, 32 lowercase hex digits: the relay's report id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>Where a send has got to: step N of M, and what that step is, for the window's progress line.</summary>
public sealed record ReportProgress(int Step, int Steps, string What);

/// <summary>The relay's limits, mirrored so the window can stop a report the relay would refuse.</summary>
public static class ReportLimits
{
    // relay/src/report.js LIMITS: 20,000 characters of text, 200 of contact, five screenshots.
    public const int TextChars = 20_000;
    public const int ContactChars = 200;
    public const int Screenshots = 5;

    // The relay takes up to 7,000,000 base64 characters per file, which is 5,250,000 bytes. A little under
    // that, so an image the app has just fitted can never land on the wrong side of the line.
    public const int FileBytes = 5_000_000;

    /// <summary>The name of the Nth screenshot (from 1), by its format.</summary>
    public static string ShotName(int number, bool jpeg) => $"shot-{number}.{(jpeg ? "jpg" : "png")}";

    public const string DumpName = "dump.txt";

    /// <summary>
    /// The dump as UTF-8, cut to the file limit if it is ever that long (a huge trace tail). The cut keeps
    /// the start, which holds the header, the screens and the bar, and says that it was cut.
    /// </summary>
    public static byte[] DumpBytes(string dump)
    {
        var bytes = Encoding.UTF8.GetBytes(dump);
        if (bytes.Length <= FileBytes) return bytes;
        const string note = "\n\n(the dump was cut here to fit the report's size limit)\n";
        // Characters, not bytes, are cut: a cut in the middle of a multi-byte character would leave invalid
        // UTF-8. A UTF-16 character is at most 3 bytes of UTF-8 (a surrogate pair is 4 bytes for 2), so a
        // quarter of the byte limit in characters is always under it.
        return Encoding.UTF8.GetBytes(dump[..Math.Min(dump.Length, FileBytes / 4)] + note);
    }
}

/// <summary>The build and Windows version, as the relay accepts them.</summary>
public static class ReportVersion
{
    /// <summary>
    /// The app's InformationalVersion, made fit for the relay. A build reports "2.5.12+&lt;40-hex commit&gt;"
    /// (Nerdbank.GitVersioning), and the relay allows at most 40 characters after the three numbers, so the
    /// commit is cut to its first 10 characters, which still names it. Anything else passes unchanged.
    /// </summary>
    public static string Of(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0 || informational.Length - plus <= 11
            ? informational
            : informational[..(plus + 11)];
    }

    /// <summary>Windows' version as the relay wants it, e.g. "10.0.26200.0".</summary>
    public static string Windows(Version version) => version.ToString();
}
