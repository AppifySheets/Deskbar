using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// A link to a file on this machine (FileLink; task f9f25172, "when giving file links, they should be clickable too").
// The text holding the link may come from a session, so the tests are mostly about what a click must NOT do: start a
// program, or reach out to a share.
public class FileLinkTests
{
    static readonly IReadOnlySet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\work\design\index.html", @"C:\work\shot.PNG", @"C:\work\setup.exe", @"C:\work\run.ps1", @"C:\work\report.docx",
        @"C:\work\page.html.lnk", @"C:\work\notes",
    };

    static readonly IReadOnlySet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\", @"C:\work", @"C:\work\design",
    };

    static (FileLinkAction Action, string Path) Plan(string path) => FileLink.Plan(path, Files.Contains, Folders.Contains);

    [Theory]
    [InlineData("file:///C:/work/design/index.html", @"C:\work\design\index.html")]
    [InlineData(@"C:\work\design\index.html", @"C:\work\design\index.html")]
    [InlineData("C:/work/design/index.html", @"C:\work\design\index.html")]
    [InlineData("file:///C:/My%20Files/a.html", @"C:\My Files\a.html")]
    public void A_path_on_a_drive_is_local(string address, string path) =>
        Assert.Equal(path, FileLink.LocalPath(new Uri(address)).GetValueOrDefault());

    // A share signs Windows in to its server; a second colon names a stream inside a file; a quote would end the
    // argument Explorer is given.
    [Theory]
    [InlineData("https://example.com/a.html")]
    [InlineData("file://server/share/a.html")]
    [InlineData(@"\\server\share\a.html")]
    [InlineData("file:///C:/work/page.html:run.exe")]
    [InlineData("file:///C:/work/a%22b.html")]
    [InlineData("ms-settings:display")]
    public void Anything_else_is_not(string address) =>
        Assert.True(FileLink.LocalPath(new Uri(address)).HasNoValue);

    [Theory]
    [InlineData(@"C:\work\design\index.html")]
    [InlineData(@"C:\work\shot.PNG")]
    public void A_document_opens(string path) => Assert.Equal((FileLinkAction.Open, path), Plan(path));

    // Never started: a program, a script, a file that can carry macros, a shortcut dressed as a page, a file with no
    // type at all.
    [Theory]
    [InlineData(@"C:\work\setup.exe")]
    [InlineData(@"C:\work\run.ps1")]
    [InlineData(@"C:\work\report.docx")]
    [InlineData(@"C:\work\page.html.lnk")]
    [InlineData(@"C:\work\notes")]
    public void Any_other_file_is_only_shown(string path) => Assert.Equal((FileLinkAction.Reveal, path), Plan(path));

    [Fact]
    public void A_folder_opens_in_Explorer() => Assert.Equal((FileLinkAction.OpenFolder, @"C:\work\design"), Plan(@"C:\work\design"));

    [Fact]
    public void A_path_that_is_gone_opens_the_nearest_folder_above_it() =>
        Assert.Equal((FileLinkAction.OpenFolder, @"C:\work\design"), Plan(@"C:\work\design\old\light\index.html"));

    [Fact]
    public void A_path_on_a_drive_that_is_not_there_does_nothing() =>
        Assert.Equal(FileLinkAction.Nothing, Plan(@"Q:\gone\index.html").Action);
}
