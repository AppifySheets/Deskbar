using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Reporting;

namespace Deskbar.Windows.Tests;

// "Report a problem…" (issue #331): the window on real WPF, with fake services in place of the relay, the disk and
// Notepad. The protocol itself is pinned in Deskbar.Core.Tests (ReportClientTests) and in relay/test.
public class ReportWindowTests
{
    // Records what the window asked of the app, and answers Send from a queue (success with #12 when it runs out).
    sealed class Fakes
    {
        public List<ProblemReport> Sent { get; } = [];
        public List<string> Archived { get; } = [];
        public List<string> Forgotten { get; } = [];
        public List<string> Viewed { get; } = [];
        public Queue<Result<int>> Answers { get; } = new();

        public ReportServices Services => new(
            Send: (report, _, _) => { Sent.Add(report); return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : Result.Success(12)); },
            Archive: report => { Archived.Add(report.Id); return Result.Success(@"C:\reports\saved"); },
            Forget: Forgotten.Add,
            OpenFolder: _ => { },
            ViewDump: Viewed.Add,
            AppVersion: "2.5.12+1a2b3c4d5e",
            WindowsBuild: "10.0.26200.0",
            Store: false,
            Pro: true);
    }

    static BitmapSource Picture(int width = 40, int height = 30)
    {
        var pixels = Enumerable.Range(0, width * height * 4).Select(i => (byte)(i * 7)).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    static ReportWindow Window(Fakes fakes, string dump = "== Deskbar debug dump\n") =>
        new(dump, fakes.Services) { Left = -32000, Top = -32000, ShowActivated = false };

    [Fact]
    public void Send_waits_for_something_in_what_happened() => StaThread.Run(() =>
    {
        var window = Window(new Fakes());

        Assert.False(window.SendButton.IsEnabled);
        window.Description.Text = "   ";
        Assert.False(window.SendButton.IsEnabled);
        window.Description.Text = "The bar vanished";
        Assert.True(window.SendButton.IsEnabled);
        window.Close();
    });

    [Fact]
    public void Up_to_five_screenshots_and_the_sixth_is_refused_with_a_note() => StaThread.Run(() =>
    {
        var window = Window(new Fakes());

        Enumerable.Range(0, 6).ToList().ForEach(_ => window.Add(ReportImages.FromBitmap(Picture())));

        Assert.Equal(5, window.ShotCount);
        Assert.Equal("Up to 5 screenshots. Remove one to add another.", window.ShotNote.Text);
        // Five thumbnails and no drop hint, since there is no room for another.
        Assert.Equal(5, window.Shots.Children.Count);
        Assert.DoesNotContain(window.DropHint, window.Shots.Children.Cast<object>());
        window.Close();
    });

    [Fact]
    public void A_picture_that_cannot_be_read_is_said_in_the_note() => StaThread.Run(() =>
    {
        var window = Window(new Fakes());

        window.Add(Result.Failure<ReportImages.Shot>("shot.bmp is not a picture Deskbar can read."));

        Assert.Equal(0, window.ShotCount);
        Assert.Equal("shot.bmp is not a picture Deskbar can read.", window.ShotNote.Text);
        window.Close();
    });

    [Fact]
    public void The_report_names_screenshots_in_order_and_adds_the_dump_when_ticked() => StaThread.Run(() =>
    {
        var window = Window(new Fakes(), dump: "the dump");
        window.Description.Text = "  The bar vanished  ";
        window.Contact.Text = " @someone ";
        window.Add(ReportImages.FromBitmap(Picture()));
        window.Add(ReportImages.FromBitmap(Picture()));

        var report = window.Compose();

        Assert.Equal("The bar vanished", report.Text);
        Assert.Equal("@someone", report.Contact);
        Assert.Equal("2.5.12+1a2b3c4d5e", report.AppVersion);
        Assert.True(report.Pro);
        Assert.Equal(["shot-1.png", "shot-2.png", "dump.txt"], report.Attachments.Select(a => a.Name));
        Assert.Equal("the dump", System.Text.Encoding.UTF8.GetString(report.Attachments[^1].Bytes));

        window.IncludeDump.IsChecked = false;
        Assert.Equal(["shot-1.png", "shot-2.png"], window.Compose().Attachments.Select(a => a.Name));
        window.Close();
    });

    [Fact]
    public void A_sent_report_shows_its_reference() => StaThread.Run(() =>
    {
        var fakes = new Fakes();
        var window = Window(fakes);
        window.Description.Text = "The bar vanished";
        window.Contact.Text = "me@example.com";

        window.SendAsync(window.Compose()).GetAwaiter().GetResult();

        Assert.Single(fakes.Sent);
        Assert.Equal("Sent, thanks. | Your reference is #12. We will write to you when we know more.", window.StateSummary);
        Assert.Equal("Close", window.CloseButton.Content);
        Assert.Empty(fakes.Archived);
        window.Close();
    });

    // Nothing is lost: a failed send saves the report and names the folder; Try again resends the SAME report (same
    // id, so the relay recognises files already up), and once it gets through the saved copy is removed.
    [Fact]
    public void A_failed_send_is_saved_and_a_successful_retry_removes_the_saved_copy() => StaThread.Run(() =>
    {
        var fakes = new Fakes();
        fakes.Answers.Enqueue(Result.Failure<int>("No connection to report.deskbar.app."));
        var window = Window(fakes);
        window.Description.Text = "The bar vanished";
        var report = window.Compose();

        window.SendAsync(report).GetAwaiter().GetResult();

        Assert.Equal(
            @"Could not send. | No connection to report.deskbar.app. The report is saved here, nothing is lost: | C:\reports\saved",
            window.StateSummary);
        Assert.Equal("Try again", window.SendButton.Content);
        Assert.Equal([report.Id], fakes.Archived);

        window.SendAsync(report).GetAwaiter().GetResult();

        Assert.Equal([report.Id, report.Id], fakes.Sent.Select(r => r.Id));
        Assert.Equal([@"C:\reports\saved"], fakes.Forgotten);
        Assert.StartsWith("Sent, thanks.", window.StateSummary);
        window.Close();
    });

    [Fact]
    public void A_send_that_throws_becomes_a_failed_send_and_not_a_crash() => StaThread.Run(() =>
    {
        var fakes = new Fakes();
        var services = fakes.Services with { Send = (_, _, _) => throw new InvalidOperationException("boom") };
        var window = new ReportWindow("dump", services) { Left = -32000, Top = -32000, ShowActivated = false };
        window.Description.Text = "The bar vanished";

        window.SendAsync(window.Compose()).GetAwaiter().GetResult();

        Assert.StartsWith("Could not send. | Something went wrong while sending: boom", window.StateSummary);
        window.Close();
    });

    [Fact]
    public void A_small_picture_is_sent_as_png() => StaThread.Run(() =>
    {
        var shot = ReportImages.FromBitmap(Picture());

        Assert.True(shot.IsSuccess);
        Assert.False(shot.Value.Jpeg);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, shot.Value.Bytes.Take(4));
    });

    // A PNG file under the limit goes exactly as it is; a BMP is re-encoded, since the relay takes PNG and JPEG only.
    [Fact]
    public void A_dropped_png_is_kept_and_a_bmp_is_converted() => StaThread.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"deskbar-shots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var png = Path.Combine(folder, "a.png");
            var bmp = Path.Combine(folder, "b.bmp");
            Save(new PngBitmapEncoder(), png);
            Save(new BmpBitmapEncoder(), bmp);

            var keptPng = ReportImages.FromFile(png);
            var convertedBmp = ReportImages.FromFile(bmp);

            Assert.Equal(File.ReadAllBytes(png), keptPng.Value.Bytes);
            Assert.False(convertedBmp.Value.Jpeg);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, convertedBmp.Value.Bytes.Take(4));
            Assert.True(ReportImages.FromFile(Path.Combine(folder, "missing.png")).IsFailure);
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    static void Save(BitmapEncoder encoder, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(Picture()));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // A capture too big for PNG under the limit (noise does not compress) comes out as a JPEG that fits.
    [Fact]
    public void A_picture_too_big_for_png_becomes_a_jpeg_under_the_limit() => StaThread.Run(() =>
    {
        const int width = 2600, height = 1600;
        var random = new Random(331);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        var noise = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        noise.Freeze();

        var shot = ReportImages.FromBitmap(noise);

        Assert.True(shot.Value.Jpeg);
        Assert.True(shot.Value.Bytes.Length <= ReportLimits.FileBytes, $"{shot.Value.Bytes.Length} bytes");
    });
}
