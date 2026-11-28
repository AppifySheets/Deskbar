using Deskbar.Core.Control;
using Xunit;

namespace Deskbar.Core.Tests;

// Which session folders count as throwaway probes (task 98a6378a): the temp folder and a Claude job's tmp folder, and
// everything below them, and nothing else.
public class ThrowawayFolderTests
{
    const string Temp = @"C:\Users\me\AppData\Local\Temp\";
    const string Profile = @"C:\Users\me";

    // The disk is never asked: a short segment expands to the long form this stand-in knows.
    static string LongPath(string path) => path.Replace(@"C:\Users\ME~1", @"C:\Users\me");

    static bool Is(string? folder) => ThrowawayFolder.Is(folder, Temp, Profile, LongPath);

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\probe-hook")]
    [InlineData(@"c:\users\me\appdata\local\temp\probe-hook\")]
    [InlineData("C:/Users/me/AppData/Local/Temp/probe-hook")]
    [InlineData(@"C:\Users\ME~1\AppData\Local\Temp\probe-hook")]
    [InlineData(@"C:\Users\me\.claude\jobs\477b53e1\tmp")]
    [InlineData(@"C:\Users\me\.claude\jobs\477b53e1\tmp\probe-hook")]
    [InlineData(@"C:\Users\me\.claude\jobs\c4a2305e\TMP\probe-proj\plan-probe")]
    public void Temp_and_job_tmp_folders_are_throwaways(string folder) => Assert.True(Is(folder));

    [Theory]
    [InlineData(@"E:\repos\TaskSpaces")]
    [InlineData(@"E:\repos\TaskSpaces\.claude\worktrees\fix")]
    [InlineData(@"C:\Users\me\AppData\Local\TempOther\x")]
    [InlineData(@"C:\Users\me\AppData\Local")]
    [InlineData(@"C:\Users\me\.claude\jobs")]
    [InlineData(@"C:\Users\me\.claude\jobs\477b53e1")]
    [InlineData(@"C:\Users\me\.claude\jobs\477b53e1\work")]
    [InlineData(@"C:\Users\me\.claude\jobs\tmp")]
    [InlineData(@"C:\Users\me")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_folders_are_not(string? folder) => Assert.False(Is(folder));

    [Fact]
    public void Unknown_roots_match_nothing() =>
        Assert.False(ThrowawayFolder.Is(@"C:\Users\me\AppData\Local\Temp\probe", "", "", LongPath));
}
