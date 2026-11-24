using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// PICTURES PASTED INTO A TASK, in and out of files. Design: docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1.
//
// Ctrl+V or a drop in a task's comment box or description editor gives PENDING pictures: bytes held in memory with a
// small thumbnail for the row under the box. Nothing is written then, so a draft thrown away leaves nothing behind. When
// the comment or description is saved, Save writes them under the state folder, task-images\<task id>\, and returns the
// relative paths the markdown points at (TaskImages, in Core, owns that format and the rules for what is ours).
//
// Encoding leans on ReportImages ("Report a problem…" takes screenshots the same way): a clipboard picture becomes a PNG,
// which keeps a screenshot's flat colour and text sharp; a dropped PNG or JPEG is kept byte for byte; any other format is
// decoded and saved as PNG. Unlike a report there is no size cap to meet, but a picture wider than 4096 px is scaled down
// to 4096: Claude Code shrinks big images itself, and this only keeps a multi-screen capture from being a huge file a
// session has to load.
//
// Thumbnails for the thread are decoded at the width the row asks for and cached by path and last-write time. A file that
// cannot be read is NOT cached as missing (CLAUDE.md: never cache a lookup failure that can succeed later), so the next
// redraw tries again.
static class TaskImageFiles
{
    /// <summary>A picture waiting to be saved: its bytes, whether they are a JPEG, and a thumbnail for the box's row.</summary>
    public sealed record Pending(byte[] Bytes, bool Jpeg, ImageSource Thumbnail);

    /// <summary>The widest picture saved, in pixels (see the header).</summary>
    public const int MaxWidth = 4096;

    static readonly ConcurrentDictionary<(string Path, DateTime Written, int Width), ImageSource> thumbnails = new();

    /// <summary>A picture from the clipboard or a drop: a PNG, scaled down to <see cref="MaxWidth"/> when wider.</summary>
    public static Result<Pending> FromBitmap(BitmapSource image) =>
        Result.Try(() => ReportImages.Encode(new PngBitmapEncoder(), Narrowed(image)), e => $"The picture could not be read: {e.Message}")
            .Map(png => new Pending(png, false, ReportImages.Thumbnail(png)));

    /// <summary>
    /// An image file that was dropped or copied in Explorer: a PNG or JPEG no wider than <see cref="MaxWidth"/> as it is,
    /// anything else decoded and handled as <see cref="FromBitmap"/>.
    /// </summary>
    public static Result<Pending> FromFile(string path) =>
        Result.Try(() => File.ReadAllBytes(path), e => $"{Path.GetFileName(path)} could not be read: {e.Message}")
            .Bind(bytes => Result.Try(() => ReportImages.Decode(bytes), _ => $"{Path.GetFileName(path)} is not a picture Deskbar can read.")
                .Bind(decoded =>
                    decoded.PixelWidth <= MaxWidth && ReportImages.StartsWith(bytes, ReportImages.PngMagic) ? Result.Success(new Pending(bytes, false, ReportImages.Thumbnail(bytes)))
                    : decoded.PixelWidth <= MaxWidth && ReportImages.StartsWith(bytes, ReportImages.JpegMagic) ? Result.Success(new Pending(bytes, true, ReportImages.Thumbnail(bytes)))
                    : FromBitmap(decoded)));

    /// <summary>
    /// The pictures in a paste or a drop: image files first (a copy in Explorer is a file list), else one bitmap (a
    /// screenshot), else none, so text alone is left to the text box. Files that do not look like pictures are skipped.
    /// </summary>
    /// <remarks>
    /// Two guards, both from the branch's final review. Every read of a clipboard or drag data object goes back to OLE and
    /// can throw (an RDP clipboard, an owner that fails delayed rendering); a throw here, on the UI thread, would restart
    /// the app, so a data object that cannot be read holds no pictures. And an Office clipboard (copied Excel or
    /// LibreOffice cells, which come with CSV or RTF beside a picture of themselves) is text: pasting cells into a comment
    /// means their words. A screenshot or a browser's "Copy image" has neither format, so it stays a picture.
    /// </remarks>
    public static IReadOnlyList<Result<Pending>> FromData(IDataObject data) =>
        Result.Try(() => Files(data) is { } files
                ? files.Where(ReportImages.LooksLikeImage).Select(FromFile).ToArray()
                : !IsOfficeText(data) && data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap
                    ? [FromBitmap(bitmap)]
                    : Array.Empty<Result<Pending>>())
            .TapError(e => ClickTrace.Write($"tasks window: a paste or drop could not be read: {e}"))
            .GetValueOrDefault([]);

    /// <summary>
    /// Whether a paste or drop would bring pictures, without reading them: for a drag's cursor. False for a data object
    /// that cannot be read, and for an Office clipboard (see <see cref="FromData"/>).
    /// </summary>
    public static bool HoldsPictures(IDataObject data) =>
        Result.Try(() => Files(data) is { } files
                ? files.Any(ReportImages.LooksLikeImage)
                : !IsOfficeText(data) && data.GetDataPresent(DataFormats.Bitmap))
            .GetValueOrDefault(false);

    static string[]? Files(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;

    static bool IsOfficeText(IDataObject data) =>
        data.GetDataPresent(DataFormats.CommaSeparatedValue) || data.GetDataPresent(DataFormats.Rtf);

    /// <summary>
    /// Writes the pictures for a task, named by <paramref name="at"/> and their place in the list, and returns their
    /// relative paths in order. All or nothing: on a failure the files already written are deleted again.
    /// </summary>
    public static Result<IReadOnlyList<string>> Save(string stateDir, Guid taskId, IReadOnlyList<Pending> images, DateTimeOffset at)
    {
        if (images.Count == 0) return Result.Success<IReadOnlyList<string>>([]);
        var relatives = images.Select((image, i) => TaskImages.RelativeFor(taskId, at, i, image.Jpeg)).ToArray();
        var paths = relatives.Select(relative => TaskImages.Resolve(relative, stateDir).Value).ToArray();
        return Result.Try(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths[0])!);
                paths.Zip(images).ToList().ForEach(pair => File.WriteAllBytes(pair.First, pair.Second.Bytes));
            }, e => $"The pictures could not be saved: {e.Message}")
            .Map(() => (IReadOnlyList<string>)relatives)
            .TapError(_ => paths.Where(File.Exists).ToList().ForEach(path => Result.Try(() => File.Delete(path))));
    }

    /// <summary>
    /// The thumbnail of one of the task's pictures at <paramref name="decodeWidth"/> pixels, frozen; none for a path
    /// that is not ours or a file that cannot be read (not remembered, see the header).
    /// </summary>
    public static Maybe<ImageSource> Thumbnail(string stateDir, string relative, int decodeWidth) =>
        TaskImages.Resolve(relative, stateDir).Bind(path =>
            Result.Try(() => File.GetLastWriteTimeUtc(path))
                .Ensure(_ => File.Exists(path), "missing")
                .Bind(written => thumbnails.TryGetValue((path, written, decodeWidth), out var cached)
                    ? Result.Success(cached)
                    : Result.Try(() => Load(path, decodeWidth)).Tap(loaded => thumbnails[(path, written, decodeWidth)] = loaded))
                .Map(Maybe.From)
                .GetValueOrDefault(Maybe<ImageSource>.None));

    static ImageSource Load(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.DecodePixelWidth = decodeWidth;
        // OnLoad reads the file now and lets go of it, so Explorer, a viewer or a task's delete is never blocked by it.
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();
        return image;
    }

    static BitmapSource Narrowed(BitmapSource image) =>
        image.PixelWidth <= MaxWidth ? image : ReportImages.Scaled(image, (double)MaxWidth / image.PixelWidth);
}
