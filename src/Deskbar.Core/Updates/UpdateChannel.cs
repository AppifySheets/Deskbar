namespace Deskbar.Core.Updates;

// The words of the update offer, by where the update comes from.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// The direct build (the loose exe from GitHub) downloads the new exe itself and starts it, so its
// question names the version and its button just says Update, exactly as before the Store existed.
//
// The Store copy cannot do that: only the Store updates a package, and the Store cannot replace a
// package that is running, which the bar always is. So the Store copy asks the Store to install the
// update, and the Store closes Deskbar to do it. Its question names the Store rather than a version
// (the Store decides which package arrives), and its button says where the update comes from.
//
// restartProven: whether Windows has been measured to bring Deskbar back after the Store's update
// (the App registers for restart before it asks). Until the spike has shown that it does, the
// question warns that Deskbar closes and has to be opened from Start again, because a bar that
// vanishes without a word looks like a crash. The App holds the flag as a constant set by the spike.
//
// Pure, so the copy is pinned by Deskbar.Core.Tests.Updates.UpdateChannelTests.
public static class UpdateChannel
{
    public static string OfferQuestion(bool packaged, string version, bool restartProven) =>
        packaged
            ? "Update Deskbar from the Store?" + (restartProven ? "" : "\n\nDeskbar will close to update; open it from Start afterwards.")
            : $"Update Deskbar to {version}?";

    public static string OfferButton(bool packaged) => packaged ? "Update from the Store" : "Update";
}
