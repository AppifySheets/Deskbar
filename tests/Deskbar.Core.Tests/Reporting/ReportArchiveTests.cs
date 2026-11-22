using System.Text;
using Deskbar.Core.Reporting;

namespace Deskbar.Core.Tests.Reporting;

// The fallback when a report cannot be sent, and the small helpers that keep a report inside the relay's limits.
public class ReportArchiveTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"deskbar-reports-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static ProblemReport Report(params ReportAttachment[] attachments) => new(
        "0123456789abcdef0123456789abcdef", "The bar vanished\nafter sleep", "", "2.5.12", "10.0.26200.0", true, true, attachments);

    [Fact]
    public void A_report_that_could_not_be_sent_is_kept_as_files()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        var folder = ReportArchive.Save(root, Report(new ReportAttachment("shot-1.png", png)), new DateTime(2026, 11, 22, 9, 12, 5));

        Assert.True(folder.IsSuccess);
        Assert.Equal(Path.Combine(root, "2026-11-22 091205 01234567"), folder.Value);
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(folder.Value, "shot-1.png")));
        var text = File.ReadAllText(Path.Combine(folder.Value, "report.txt"));
        Assert.Contains("The bar vanished\nafter sleep", text);
        Assert.Contains("Deskbar 2.5.12 (Store build), Deskbar Pro on Windows 10.0.26200.0", text);
        Assert.Contains("Contact: none given", text);
        Assert.Contains("Files: shot-1.png", text);
    }

    [Fact]
    public void Forget_removes_a_saved_report_and_ignores_one_already_gone()
    {
        var folder = ReportArchive.Save(root, Report(), DateTime.Now).Value;

        ReportArchive.Forget(folder);
        ReportArchive.Forget(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("2.5.12+1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", "2.5.12+1a2b3c4d5e")]
    [InlineData("2.5.12+1a2b3c4d5e", "2.5.12+1a2b3c4d5e")]
    [InlineData("2.5.12", "2.5.12")]
    [InlineData("2.5.12-g1a2b3c4d5e+1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", "2.5.12-g1a2b3c4d5e+1a2b3c4d5e")]
    public void The_version_sent_keeps_the_commit_short_enough_for_the_relay(string informational, string expected) =>
        Assert.Equal(expected, ReportVersion.Of(informational));

    [Fact]
    public void Screenshots_are_named_as_the_relay_expects()
    {
        Assert.Equal("shot-1.png", ReportLimits.ShotName(1, jpeg: false));
        Assert.Equal("shot-5.jpg", ReportLimits.ShotName(5, jpeg: true));
    }

    [Fact]
    public void A_normal_dump_is_sent_whole()
    {
        var dump = "== Deskbar debug dump\n  version 2.5.12\n";

        Assert.Equal(Encoding.UTF8.GetBytes(dump), ReportLimits.DumpBytes(dump));
    }

    [Fact]
    public void An_enormous_dump_is_cut_to_the_file_limit_and_says_so()
    {
        var dump = "== Deskbar debug dump\n" + new string('ქ', 3_000_000);

        var bytes = ReportLimits.DumpBytes(dump);

        Assert.True(bytes.Length <= ReportLimits.FileBytes);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("== Deskbar debug dump", text);
        Assert.EndsWith("(the dump was cut here to fit the report's size limit)\n", text);
    }
}
