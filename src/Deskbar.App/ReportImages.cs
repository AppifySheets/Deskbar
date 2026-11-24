using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Reporting;

namespace Deskbar.App;

// Screenshots for "Report a problem…" (ReportWindow, issue #331): turns what the user pastes or drops into
// bytes the relay accepts, a PNG or a JPEG of at most ReportLimits.FileBytes (5 MB).
//
// PNG first, because a screenshot of a desktop is flat colour and text, which PNG keeps sharp and usually
// small. A big multi-screen capture can still pass the limit, so then JPEG at quality 85, and if even that
// is too big, the same picture at half the size, repeated until it fits. A PNG or JPEG file that is already
// under the limit is sent exactly as it is, so a dropped file is not re-encoded for nothing.
static class ReportImages
{
    /// <summary>A screenshot ready to send: its bytes, whether they are a JPEG, and a small picture for the window.</summary>
    public sealed record Shot(byte[] Bytes, bool Jpeg, ImageSource Thumbnail);

    internal static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    internal static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    // The thumbnail's width in pixels: the window shows them at 92 DIP, so twice that stays sharp at 200%.
    const int ThumbnailWidth = 184;

    /// <summary>A picture from the clipboard (Win+Shift+S, Print Screen, a copied image).</summary>
    public static Result<Shot> FromBitmap(BitmapSource image) =>
        Result.Try(() => Fit(image), e => $"The picture could not be read: {e.Message}")
            .Map(fitted => new Shot(fitted.Bytes, fitted.Jpeg, Thumbnail(fitted.Bytes)));

    /// <summary>An image file that was dropped or copied in Explorer. PNG and JPEG under the limit go as they are.</summary>
    public static Result<Shot> FromFile(string path) =>
        Result.Try(() => File.ReadAllBytes(path), e => $"{Path.GetFileName(path)} could not be read: {e.Message}")
            .Bind(bytes =>
                bytes.Length <= ReportLimits.FileBytes && StartsWith(bytes, PngMagic) ? Result.Success((Bytes: bytes, Jpeg: false))
                : bytes.Length <= ReportLimits.FileBytes && StartsWith(bytes, JpegMagic) ? Result.Success((Bytes: bytes, Jpeg: true))
                : Result.Try(() => Fit(Decode(bytes)), _ => $"{Path.GetFileName(path)} is not a picture Deskbar can read."))
            .Map(fitted => new Shot(fitted.Bytes, fitted.Jpeg, Thumbnail(fitted.Bytes)));

    /// <summary>Whether a dropped or copied file looks like a picture, by its extension, before reading it.</summary>
    public static bool LooksLikeImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp";

    internal static bool StartsWith(byte[] bytes, byte[] magic) => bytes.Length >= magic.Length && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);

    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    // PNG if it fits, else JPEG, else JPEG at half the size until it does (the order is explained at the top of the file).
    static (byte[] Bytes, bool Jpeg) Fit(BitmapSource image)
    {
        var png = Encode(new PngBitmapEncoder(), image);
        if (png.Length <= ReportLimits.FileBytes) return (png, false);

        // The halvings are a sequence that ends at the first size that fits; 8 halvings take a 16K-wide
        // capture down to 64 pixels, past which there is nothing left worth sending.
        return Enumerable.Range(0, 9)
            .Select(halvings => Encode(new JpegBitmapEncoder { QualityLevel = 85 }, Scaled(image, Math.Pow(0.5, halvings))))
            .Select(jpeg => (Bytes: jpeg, Jpeg: true))
            .FirstOrDefault(candidate => candidate.Bytes.Length <= ReportLimits.FileBytes,
                (Bytes: Encode(new JpegBitmapEncoder { QualityLevel = 60 }, Scaled(image, 1.0 / 512)), Jpeg: true));
    }

    internal static BitmapSource Scaled(BitmapSource image, double factor) =>
        factor >= 1 ? image : new TransformedBitmap(image, new ScaleTransform(factor, factor));

    internal static byte[] Encode(BitmapEncoder encoder, BitmapSource image)
    {
        // JPEG has no alpha channel, so the picture is converted to plain 24-bit colour first and any alpha is
        // dropped. Screenshots are opaque, so in practice nothing is lost. PNG keeps the alpha.
        var source = encoder is JpegBitmapEncoder ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : image;
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static ImageSource Thumbnail(byte[] bytes)
    {
        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.StreamSource = new MemoryStream(bytes);
        thumbnail.DecodePixelWidth = ThumbnailWidth;
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.EndInit();
        // Frozen: the window's thread owns it otherwise, and a frozen image is also cheaper to draw.
        thumbnail.Freeze();
        return thumbnail;
    }
}
