using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests.Control;

// PipeTrust.Trusted: whether the control client sends its request to the pipe that answered (security review of
// fb50f0b, M-3). Pure, so every case is a line here: the pipe's owner and mandatory label against the client's user,
// default owner and elevation. Opens no window.
public class PipeTrustTests
{
    const string User = "S-1-5-21-1-2-3-1001";
    const string OtherUser = "S-1-5-21-1-2-3-1002";
    const string Administrators = "S-1-5-32-544";
    const string Low = "S-1-16-4096";
    const string Medium = "S-1-16-8192";
    const string High = "S-1-16-12288";

    // A non-elevated Deskbar and a non-elevated client: the pipe is the user's, with no label (medium).
    [Fact]
    public void A_medium_pipe_of_this_user_is_trusted() =>
        Assert.True(PipeTrust.Trusted(owner: User, user: User, defaultOwner: User, elevated: false, label: null));

    // An elevated Deskbar owns its pipe as Administrators; an elevated PowerShell or cmd client has Administrators as
    // its default owner too.
    [Fact]
    public void An_elevated_pipe_and_an_elevated_client_with_administrators_as_owner() =>
        Assert.True(PipeTrust.Trusted(Administrators, User, defaultOwner: Administrators, elevated: true, label: null));

    // An elevated client started by Git Bash (MSYS sets the user as its processes' default owner) still trusts an elevated
    // Deskbar's pipe: the measured CI case.
    [Fact]
    public void An_elevated_msys_client_trusts_an_elevated_deskbar() =>
        Assert.True(PipeTrust.Trusted(Administrators, User, defaultOwner: User, elevated: true, label: null));

    [Fact]
    public void Another_users_pipe_is_not_trusted()
    {
        Assert.False(PipeTrust.Trusted(OtherUser, User, User, elevated: false, label: null));
        Assert.False(PipeTrust.Trusted(OtherUser, User, Administrators, elevated: true, label: null));
        Assert.False(PipeTrust.Trusted(null, User, User, elevated: false, label: null));
    }

    // Administrators owning the pipe means something elevated made it; a client that is not elevated has no reason to
    // trust that.
    [Fact]
    public void An_administrators_pipe_is_not_trusted_by_a_client_that_is_not_elevated() =>
        Assert.False(PipeTrust.Trusted(Administrators, User, User, elevated: false, label: null));

    // Security review I1: a Low-integrity process of this user (a sandboxed browser tab's helper, say) can make a pipe of
    // the name, owned by this user, labelled Low. It is not Deskbar, and its reply would go into a session's context.
    [Fact]
    public void A_pipe_labelled_below_medium_is_not_trusted()
    {
        Assert.False(PipeTrust.Trusted(User, User, User, elevated: false, label: Low));
        Assert.False(PipeTrust.Trusted(User, User, User, elevated: false, label: "S-1-16-0"));
        Assert.False(PipeTrust.Trusted(User, User, User, elevated: false, label: "not a label"));
        Assert.True(PipeTrust.Trusted(User, User, User, elevated: false, label: Medium));
        Assert.True(PipeTrust.Trusted(Administrators, User, Administrators, elevated: true, label: High));
    }
}
