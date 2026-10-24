using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// The "already running" dialog says WHICH Deskbar is running, so someone with both the Store copy and the
// direct build knows which one to untick in Manage. The loser of the single-instance race reads the other
// process's exe path and hands it here. docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
public class OtherInstanceTests
{
    [Fact]
    public void Store_path_is_named() =>
        Assert.StartsWith("The copy installed from the Microsoft Store is running.",
            OtherInstance.Describe([@"C:\Program Files\WindowsApps\Petre.Deskbar_2.1.44.0_x64__n8ch8v53xw03y\Deskbar.exe"]));

    [Fact]
    public void Folder_is_named() =>
        Assert.StartsWith(@"The copy at C:\Users\x\Programs\Deskbar is running.",
            OtherInstance.Describe([@"C:\Users\x\Programs\Deskbar\Deskbar-2.1.40-win-x64.exe"]));

    // An elevated copy cannot be read by an unelevated one, which is the null.
    [Fact]
    public void Unreadable_path_falls_back() =>
        Assert.StartsWith("Deskbar is already running.", OtherInstance.Describe([null]));

    [Fact]
    public void No_other_process_found_falls_back() =>
        Assert.Equal("Deskbar is already running.", OtherInstance.Describe([]));

    // A readable path wins over an unreadable one, whichever order they were found in.
    [Fact]
    public void A_readable_path_is_preferred_to_an_unreadable_one() =>
        Assert.Equal(@"The copy at D:\Apps is running.", OtherInstance.Describe([null, @"D:\Apps\Deskbar.exe"]));

    // The release exe is named after its version, so its process is not called "Deskbar".
    [Theory]
    [InlineData("Deskbar", true)]
    [InlineData("deskbar", true)]
    [InlineData("Deskbar-2.1.40-win-x64", true)]
    [InlineData("Deskbar-2.0.0-win-x64", true)]
    [InlineData("deskbar-cli", false)]
    [InlineData("TaskSpaces-1.17.0-win-x64", false)]
    [InlineData("DeskbarHelper", false)]
    public void Recognises_deskbar_process_names(string processName, bool expected) =>
        Assert.Equal(expected, OtherInstance.IsDeskbarProcessName(processName));
}
