using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// A warning box of ours (the bar's "Delete workspace..." one) showed on the floating bar as a row in
// "Unplaced" while it was open. Our own windows are listed on purpose, so Manage has a row; a dialog of
// ours is not one of those. IsOurDialog is the pure rule behind TopLevelWindows.IsListed.
// The windows themselves are not created here: the real box on the real bar is checked by hand.
public class OurDialogTests
{
    static readonly nint SomeOwner = 0x1234;

    [Theory]
    // Our process, with an owner: PromptDialog and the message boxes the bar owns. A dialog, so no row.
    [InlineData(true, true, "HwndWrapper[Deskbar;;abc]", true)]
    // Our process, no owner, Windows' dialog class: MessageBox.Show(text) with no owner. A dialog.
    [InlineData(true, false, "#32770", true)]
    // Our process, no owner, a normal WPF class: Manage. Not a dialog, so it keeps its row.
    [InlineData(true, false, "HwndWrapper[Deskbar;;abc]", false)]
    // Another app's owned window: not ours, so it stays listed.
    [InlineData(false, true, "Notepad", false)]
    // Another app's message box: not ours, so it stays listed.
    [InlineData(false, false, "#32770", false)]
    public void Only_our_own_dialogs_are_dialogs(bool ourProcess, bool hasOwner, string className, bool expected) =>
        Assert.Equal(expected, TopLevelWindows.IsOurDialog(ourProcess, hasOwner ? SomeOwner : 0, className));
}
