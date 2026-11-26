using System.IO;
using System.Text;
using System.Windows;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.App;

// Reads what was dropped on a workspace's bar into a BookmarkDrop (the plain record Core parses). This is the only
// place that touches the OLE data object, so Core stays free of WPF and the parsing stays testable.
//
// The formats a browser's address-bar drag offers were NOT measured (probes note, decision d), so every one a link
// might arrive in is tried, in this order: UniformResourceLocatorW (UTF-16), UniformResourceLocator (ANSI),
// UnicodeText. The page title Chromium sends rides in FileGroupDescriptorW as the name of a ".url" file.
//
// Nothing here may throw: it runs inside DragOver and Drop, where an exception would take down the drag loop.
// A malformed data object is read as "nothing there".
static class BookmarkDropData
{
    const string UrlW = "UniformResourceLocatorW";
    const string UrlAnsi = "UniformResourceLocator";
    const string Descriptor = "FileGroupDescriptorW";

    // The first cFileName in a FILEGROUPDESCRIPTORW: a 4 byte count, then 72 bytes of FILEDESCRIPTORW fields
    // (flags, clsid, size, point, attributes, three FILETIMEs, two size words) before the 520 byte name.
    const int FirstNameOffset = 76;

    static readonly string[] Formats = [UrlW, UrlAnsi, Descriptor, DataFormats.UnicodeText, DataFormats.FileDrop];

    // None for a Deskbar window drag (that is the bar's own gesture and keeps its own path) and for data holding
    // none of the formats a bookmark can come from.
    internal static Maybe<BookmarkDrop> From(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DraggedWindow.DragFormat) || !Formats.Any(data.GetDataPresent))
                return Maybe<BookmarkDrop>.None;

            var text = TextOf(data, DataFormats.UnicodeText, Encoding.Unicode);
            var url = TextOf(data, UrlW, Encoding.Unicode) ?? TextOf(data, UrlAnsi, Encoding.Default) ?? text;
            var files = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped
                ? dropped
                : [];
            return new BookmarkDrop(url, TitleOf(data), text, files);
        }
        catch (Exception)
        {
            return Maybe<BookmarkDrop>.None;
        }
    }

    // One text format as a string, or null when it is absent or empty. Managed callers hand a string, OLE hands a
    // stream or bytes, so all three are read. A trailing NUL (the Win32 convention) and surrounding blanks go.
    static string? TextOf(IDataObject data, string format, Encoding encoding) =>
        data.GetDataPresent(format)
            ? (data.GetData(format) switch
            {
                string s => s,
                MemoryStream stream => encoding.GetString(stream.ToArray()),
                byte[] bytes => encoding.GetString(bytes),
                _ => null,
            })?.Split('\0')[0].Trim() is { Length: > 0 } text ? text : null
            : null;

    // The first descriptor's file name, "Portainer.url" for a Chromium page drag; null when there is none. The parser
    // strips the ".url", and a missing title makes the bookmark be named after the link's host.
    static string? TitleOf(IDataObject data) =>
        data.GetDataPresent(Descriptor) && data.GetData(Descriptor) is MemoryStream stream
            ? stream.ToArray() is { Length: > FirstNameOffset + 1 } bytes
                ? Encoding.Unicode.GetString(bytes, FirstNameOffset, Math.Min(520, bytes.Length - FirstNameOffset) & ~1)
                    .Split('\0')[0].Trim() is { Length: > 0 } name ? name : null
                : null
            : null;
}
