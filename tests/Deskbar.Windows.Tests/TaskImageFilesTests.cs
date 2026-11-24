using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// TaskImageFiles: what Petre pastes or drops into a task becomes a pending picture, and a file under the state folder
// when the comment or description is saved (docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1). Each test
// works in a temporary state folder of its own.
public sealed class TaskImageFilesTests : IDisposable
{
    static readonly Guid Task = Guid.Parse("d4ea6cbe-12ef-420c-8847-fa40c302ac48");
    static readonly DateTimeOffset At = new(2026, 11, 24, 5, 6, 7, 89, TimeSpan.Zero);

    readonly string root = Directory.CreateTempSubdirectory("deskbar-task-images-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    static BitmapSource Picture(int width = 40, int height = 30)
    {
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    static BitmapSource Decoded(byte[] bytes) =>
        BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

    static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // A picture wider than 4096 px is scaled down to 4096 wide, so a session is never handed a huge image.
    [Fact]
    public void FromBitmap_scales_a_wide_picture_to_4096() => StaThread.Run(() =>
    {
        var pending = TaskImageFiles.FromBitmap(Picture(5000, 10)).Value;

        Assert.False(pending.Jpeg);
        Assert.Equal(4096, Decoded(pending.Bytes).PixelWidth);
    });

    [Fact]
    public void FromBitmap_keeps_a_small_picture_its_size() => StaThread.Run(() =>
        Assert.Equal(40, Decoded(TaskImageFiles.FromBitmap(Picture()).Value.Bytes).PixelWidth));

    // A PNG dropped from Explorer is kept exactly as it is: nothing to gain from re-encoding it.
    [Fact]
    public void FromFile_keeps_a_small_png_byte_for_byte() => StaThread.Run(() =>
    {
        var bytes = Png(Picture());
        var path = Path.Combine(root, "dropped.png");
        File.WriteAllBytes(path, bytes);

        var pending = TaskImageFiles.FromFile(path).Value;

        Assert.Equal(bytes, pending.Bytes);
        Assert.False(pending.Jpeg);
    });

    [Fact]
    public void FromFile_of_something_that_is_not_a_picture_fails() => StaThread.Run(() =>
    {
        var path = Path.Combine(root, "notes.png");
        File.WriteAllText(path, "not a picture");

        Assert.True(TaskImageFiles.FromFile(path).IsFailure);
    });

    [Fact]
    public void Save_writes_files_and_returns_relative_paths() => StaThread.Run(() =>
    {
        var images = new[] { TaskImageFiles.FromBitmap(Picture()).Value, TaskImageFiles.FromBitmap(Picture(20, 20)).Value };

        var saved = TaskImageFiles.Save(root, Task, images, At).Value;

        Assert.Equal([TaskImages.RelativeFor(Task, At, 0, jpeg: false), TaskImages.RelativeFor(Task, At, 1, jpeg: false)], saved);
        Assert.All(saved, relative => Assert.True(File.Exists(TaskImages.Resolve(relative, root).Value)));
        Assert.Equal(images[1].Bytes, File.ReadAllBytes(TaskImages.Resolve(saved[1], root).Value));
    });

    // A thumbnail is decoded at the width asked for; a missing file is none, and that is NOT remembered (the CLAUDE.md
    // trap: never cache a lookup failure that can succeed later), so once the file is there the thumbnail comes.
    [Fact]
    public void Thumbnail_of_a_missing_file_is_none_and_retries() => StaThread.Run(() =>
    {
        var relative = TaskImages.RelativeFor(Task, At, 0, jpeg: false);

        Assert.True(TaskImageFiles.Thumbnail(root, relative, 64).HasNoValue);

        TaskImageFiles.Save(root, Task, [TaskImageFiles.FromBitmap(Picture(200, 100)).Value], At);
        var thumbnail = TaskImageFiles.Thumbnail(root, relative, 64);

        Assert.True(thumbnail.HasValue);
        Assert.Equal(64, ((BitmapSource)thumbnail.Value).PixelWidth);
        Assert.True(thumbnail.Value.IsFrozen);
    });

    [Fact]
    public void Thumbnail_of_a_path_that_is_not_ours_is_none() => StaThread.Run(() =>
        Assert.True(TaskImageFiles.Thumbnail(root, "task-images/../state.json", 64).HasNoValue));

    [Fact]
    public void FromData_takes_image_files_then_a_bitmap_and_ignores_text() => StaThread.Run(() =>
    {
        var path = Path.Combine(root, "a.png");
        File.WriteAllBytes(path, Png(Picture()));
        var files = new DataObject(DataFormats.FileDrop, new[] { path, Path.Combine(root, "notes.txt") });
        var bitmap = new DataObject(DataFormats.Bitmap, Picture());
        var text = new DataObject(DataFormats.UnicodeText, "just words");

        Assert.Single(TaskImageFiles.FromData(files));
        Assert.Single(TaskImageFiles.FromData(bitmap));
        Assert.Empty(TaskImageFiles.FromData(text));
    });
}
