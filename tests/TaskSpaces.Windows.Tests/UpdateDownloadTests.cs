using System.IO;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// The update download reports its progress (Petre: "show download progress, not just a random bar"). It used
// Stream.CopyToAsync, which reports nothing, so the window's bar stayed indeterminate for the whole download.
public class UpdateDownloadTests
{
    [Fact]
    public async Task The_copy_reports_its_progress_and_ends_at_the_full_size()
    {
        var data = new byte[1024 * 1024];
        new Random(7).NextBytes(data);
        using var source = new MemoryStream(data);
        using var target = new MemoryStream();
        var reports = new List<(long Bytes, long Total)>();

        await UpdateService.CopyWithProgress(source, target, data.Length, (bytes, total) => reports.Add((bytes, total)), CancellationToken.None);

        Assert.Equal(data, target.ToArray());
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(data.Length, r.Total));
        Assert.Equal(data.Length, reports[^1].Bytes);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Bytes >= p.First.Bytes), "progress went backwards");
    }

    // No Content-Length from the server: the total is reported as zero, and the window shows megabytes alone.
    [Fact]
    public async Task Without_a_known_size_the_total_is_zero()
    {
        using var source = new MemoryStream(new byte[300_000]);
        using var target = new MemoryStream();
        var reports = new List<(long Bytes, long Total)>();

        await UpdateService.CopyWithProgress(source, target, 0, (bytes, total) => reports.Add((bytes, total)), CancellationToken.None);

        Assert.Equal(300_000, reports[^1].Bytes);
        Assert.All(reports, r => Assert.Equal(0, r.Total));
    }
}
