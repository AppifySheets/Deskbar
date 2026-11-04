using Deskbar.Windows.Activation;

namespace Deskbar.Windows.Tests;

// Force close on a Store app must end the app, not the shared ApplicationFrameHost that owns its window (which
// closes every Store app at once). The pick is pure so it can be tested without a real Store window.
public class WindowActivatorFrameHostTests
{
    [Fact]
    public void A_child_in_another_process_is_the_hosted_app() =>
        Assert.Equal(900u, WindowActivator.PickHostedProcess(100, [100, 100, 900, 950]));

    [Fact]
    public void Children_in_the_frames_own_process_pick_nothing() =>
        Assert.Null(WindowActivator.PickHostedProcess(100, [100, 100]));

    [Fact]
    public void No_children_pick_nothing() =>
        Assert.Null(WindowActivator.PickHostedProcess(100, []));

    [Fact]
    public void A_child_with_no_process_is_ignored() =>
        Assert.Equal(900u, WindowActivator.PickHostedProcess(100, [0, 900]));
}
