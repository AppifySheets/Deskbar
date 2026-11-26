using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Tests.Bookmarks;

// Turning a drop (a URL, text or files) into a bookmark.
public sealed class BookmarkDropTests
{
    // Answers from dictionaries; counts every call so a test can assert that no IO happened.
    sealed class FakeSources : IBookmarkSources
    {
        public Dictionary<string, ShortcutTarget> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }

        public Maybe<ShortcutTarget> ResolveShortcut(string lnkPath) { Calls++; return Shortcuts.TryGetValue(lnkPath, out var s) ? s : Maybe<ShortcutTarget>.None; }
        public string? DescribeExe(string exePath) { Calls++; return Descriptions.GetValueOrDefault(exePath); }
        public string? NameOfApp(string appId) { Calls++; return null; }
        public string? ReadText(string path) { Calls++; return Texts.GetValueOrDefault(path); }
    }

    static BookmarkDrop Files(params string[] files) => new(null, null, null, files);

    [Fact]
    public void A_chromium_url_drop_is_named_after_the_page_title()
    {
        var bookmark = BookmarkDropParser.Parse(new("https://portainer.local:9443/", "Portainer.url", null, []), new FakeSources()).Value;

        Assert.Equal(BookmarkKind.Link, bookmark.Kind);
        Assert.Equal("Portainer", bookmark.Name);
        Assert.Equal("https://portainer.local:9443/", bookmark.Target);
    }

    [Fact]
    public void A_url_with_no_title_is_named_after_its_host() =>
        Assert.Equal("portainer.local", BookmarkDropParser.Parse(new("https://portainer.local:9443/", null, null, []), new FakeSources()).Value.Name);

    [Fact]
    public void Plain_text_that_is_a_link_becomes_a_link()
    {
        var bookmark = BookmarkDropParser.Parse(new(null, null, " https://example.org/a ", []), new FakeSources()).Value;

        Assert.Equal(BookmarkKind.Link, bookmark.Kind);
        Assert.Equal("https://example.org/a", bookmark.Target);
        Assert.Equal("example.org", bookmark.Name);
    }

    [Fact]
    public void Plain_text_that_is_not_a_link_is_refused_with_a_reason()
    {
        var result = BookmarkDropParser.Parse(new(null, null, "hello world", []), new FakeSources());

        Assert.True(result.IsFailure);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Only_the_first_file_counts()
    {
        var sources = new FakeSources();
        sources.Descriptions[@"C:\Tools\a.exe"] = "A";

        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Tools\a.exe", @"C:\Tools\b.exe"), sources).Value;

        Assert.Equal("A", bookmark.Name);
        Assert.Equal(@"C:\Tools\a.exe", bookmark.Target);
    }

    [Fact]
    public void An_exe_becomes_a_program_named_by_its_description()
    {
        var sources = new FakeSources();
        sources.Descriptions[@"C:\Tools\rustdesk.exe"] = "RustDesk";

        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Tools\rustdesk.exe"), sources).Value;

        Assert.Equal(BookmarkKind.Program, bookmark.Kind);
        Assert.Equal("RustDesk", bookmark.Name);
    }

    [Fact]
    public void An_exe_with_no_description_is_named_after_its_file()
    {
        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Tools\rustdesk.exe"), new FakeSources()).Value;

        Assert.Equal("rustdesk", bookmark.Name);
    }

    [Fact]
    public void An_internet_shortcut_file_becomes_a_link()
    {
        var sources = new FakeSources();
        sources.Texts[@"C:\Users\p\Desktop\Wiki.url"] = "[InternetShortcut]\r\nURL=https://x.org/\r\n";

        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Users\p\Desktop\Wiki.url"), sources).Value;

        Assert.Equal(BookmarkKind.Link, bookmark.Kind);
        Assert.Equal("https://x.org/", bookmark.Target);
        Assert.Equal("Wiki", bookmark.Name);
    }

    [Fact]
    public void An_internet_shortcut_with_a_hostile_scheme_is_refused()
    {
        var sources = new FakeSources();
        sources.Texts[@"C:\a.url"] = "[InternetShortcut]\r\nURL=file:///C:/Windows/System32/calc.exe\r\n";

        Assert.True(BookmarkDropParser.Parse(Files(@"C:\a.url"), sources).IsFailure);
    }

    [Fact]
    public void UrlOf_reads_only_the_internet_shortcut_section()
    {
        const string ini = "[Other]\r\nURL=https://no.example/\r\n[InternetShortcut]\r\nurl=https://yes.example/\r\n";

        Assert.Equal("https://yes.example/", InternetShortcut.UrlOf(ini).Value);
        Assert.True(InternetShortcut.UrlOf("[InternetShortcut]\r\nIconIndex=0").HasNoValue);
    }

    [Fact]
    public void A_shortcut_to_an_exe_keeps_its_arguments_and_folder()
    {
        var sources = new FakeSources();
        sources.Shortcuts[@"C:\Links\Remote.lnk"] = new(@"C:\Tools\rustdesk.exe", "--connect 1", @"C:\Tools", null);

        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Links\Remote.lnk"), sources).Value;

        Assert.Equal(BookmarkKind.Program, bookmark.Kind);
        Assert.Equal("Remote", bookmark.Name);
        Assert.Equal(@"C:\Tools\rustdesk.exe", bookmark.Target);
        Assert.Equal("--connect 1", bookmark.Arguments);
        Assert.Equal(@"C:\Tools", bookmark.WorkingDirectory);
    }

    [Fact]
    public void A_shortcut_with_only_an_app_id_becomes_an_app()
    {
        var sources = new FakeSources();
        sources.Shortcuts[@"C:\Links\Calc.lnk"] = new(null, null, null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        var bookmark = BookmarkDropParser.Parse(Files(@"C:\Links\Calc.lnk"), sources).Value;

        Assert.Equal(BookmarkKind.App, bookmark.Kind);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", bookmark.Target);
        Assert.Equal("Calc", bookmark.Name);
    }

    [Fact]
    public void A_shortcut_to_a_document_is_refused()
    {
        var sources = new FakeSources();
        sources.Shortcuts[@"C:\Links\Notes.lnk"] = new(@"C:\Docs\notes.txt", null, null, null);

        Assert.True(BookmarkDropParser.Parse(Files(@"C:\Links\Notes.lnk"), sources).IsFailure);
    }

    [Fact]
    public void Any_other_file_is_refused_with_the_standard_reason()
    {
        var result = BookmarkDropParser.Parse(Files(@"C:\Docs\notes.txt"), new FakeSources());

        Assert.Equal("Only links, programs and shortcuts can be bookmarked.", result.Error);
    }

    [Fact]
    public void Precheck_refuses_a_text_file_without_reading_it()
    {
        var sources = new FakeSources();

        var result = BookmarkDropParser.Precheck(Files(@"C:\Docs\notes.txt"));

        Assert.True(result.IsFailure);
        Assert.Equal("Only links, programs and shortcuts can be bookmarked.", result.Error);
        Assert.Equal(0, sources.Calls); // Precheck takes no sources at all: it cannot do IO
    }

    [Theory]
    [InlineData(@"C:\a\b.exe")]
    [InlineData(@"C:\a\b.lnk")]
    [InlineData(@"C:\a\b.url")]
    public void Precheck_accepts_the_file_kinds_parse_can_read(string path) =>
        Assert.True(BookmarkDropParser.Precheck(Files(path)).IsSuccess);

    [Fact]
    public void Precheck_judges_links_by_the_same_rule_as_parse()
    {
        Assert.True(BookmarkDropParser.Precheck(new("https://a.org/", null, null, [])).IsSuccess);
        Assert.True(BookmarkDropParser.Precheck(new("javascript:alert(1)", null, null, [])).IsFailure);
        Assert.True(BookmarkDropParser.Precheck(new(null, null, "just words", [])).IsFailure);
    }
}
