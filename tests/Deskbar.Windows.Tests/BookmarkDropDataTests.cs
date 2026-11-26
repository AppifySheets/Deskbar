using System.IO;
using System.Text;
using System.Windows;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// BookmarkDropData reads an OLE data object into the plain record Core parses (Task 9). The data objects here are
// WPF DataObjects filled the way a browser fills one: the URL as UTF-16 text with a trailing NUL and the page title
// as the name inside a FileGroupDescriptorW stream.
public class BookmarkDropDataTests
{
    // A FILEGROUPDESCRIPTORW with one descriptor: 4 byte count, 72 bytes of fields, 520 bytes of cFileName.
    internal static MemoryStream Descriptor(string fileName)
    {
        var bytes = new byte[4 + 592];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        Encoding.Unicode.GetBytes(fileName).CopyTo(bytes, 76);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void A_chromium_drag_gives_the_url_and_the_page_title()
    {
        var data = new DataObject();
        data.SetData("UniformResourceLocatorW", "https://portainer.local:9443/\0");
        data.SetData("FileGroupDescriptorW", Descriptor("Portainer.url"));

        var drop = BookmarkDropData.From(data).Value;

        Assert.Equal("https://portainer.local:9443/", drop.Url);
        Assert.Equal("Portainer.url", drop.UrlTitle);
    }

    [Fact]
    public void A_file_drop_gives_the_files()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\tools\rustdesk.exe", @"C:\other.lnk" });

        var drop = BookmarkDropData.From(data).Value;

        Assert.Equal([@"C:\tools\rustdesk.exe", @"C:\other.lnk"], drop.Files);
        Assert.Null(drop.Url);
    }

    [Fact]
    public void Plain_text_is_read_as_text_and_as_a_link_candidate()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "https://example.org/a");

        var drop = BookmarkDropData.From(data).Value;

        Assert.Equal("https://example.org/a", drop.Text);
        Assert.Equal("https://example.org/a", drop.Url);
    }

    [Fact]
    public void A_window_drag_is_not_a_bookmark()
    {
        var data = new DataObject();
        data.SetData(DraggedWindow.DragFormat, new DraggedWindow(new WindowHandle(101), "pinned"));
        data.SetData(DataFormats.UnicodeText, "https://example.org/a");

        Assert.True(BookmarkDropData.From(data).HasNoValue);
    }

    [Fact]
    public void Data_with_none_of_the_formats_is_not_a_bookmark() =>
        Assert.True(BookmarkDropData.From(new DataObject("Something.Else", "x")).HasNoValue);

    [Fact]
    public void A_malformed_descriptor_loses_the_title_and_nothing_else()
    {
        var data = new DataObject();
        data.SetData("UniformResourceLocatorW", "https://example.org/");
        data.SetData("FileGroupDescriptorW", new MemoryStream([1, 0, 0]));

        var drop = BookmarkDropData.From(data).Value;

        Assert.Equal("https://example.org/", drop.Url);
        Assert.Null(drop.UrlTitle);
    }
}
