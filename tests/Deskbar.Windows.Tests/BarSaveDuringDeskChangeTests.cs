using System.Reflection;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Geometry;

namespace Deskbar.Windows.Tests;

// The bar saves its spot only on the desk it last settled on (FloatingBar.DeskChanging). Petre, back at his
// desk after an RDP session: "deskbar was still attached to another monitor's edge today". The shell re-docked
// the bar 29 times on the returning desk before the debounced readjustment ran, every re-dock saved the RDP
// session's spot under the returning desk's key, and the readjustment then docked the bar where that spot said.
public class BarSaveDuringDeskChangeTests
{
    // The keys from Petre's state.json: his desk (a 4K screen left of a 4K primary), the same desk with the left
    // screen back 17 px higher, and the RDP session's single screen.
    const string Desk = "-3840,246,0,2406|0,0,3840,2160";
    const string DeskShifted = "-3840,229,0,2389|0,0,3840,2160";
    const string Rdp = "0,0,7680,2406";

    [Theory]
    // Back from RDP: the desk attached is not the one settled on, so nothing is saved until the readjustment runs.
    [InlineData(Rdp, Desk, true)]
    // Off to RDP: the same, the other way round.
    [InlineData(Desk, Rdp, true)]
    // The left screen came back a few pixels lower: the same desk, and the bar saves as usual.
    [InlineData(Desk, DeskShifted, false)]
    [InlineData(Desk, Desk, false)]
    // No layout known at the last settle (compatibility mode, a failed query): saves as it always did.
    [InlineData(MonitorLayoutKey.Unknown, Desk, false)]
    [InlineData(MonitorLayoutKey.Unknown, MonitorLayoutKey.Unknown, false)]
    // The monitors cannot be read right now while a desk was settled on: mid-change, so wait.
    [InlineData(Desk, MonitorLayoutKey.Unknown, true)]
    public void The_desk_is_changing_only_when_another_desk_is_attached(string settled, string attached, bool changing) =>
        Assert.Equal(changing, FloatingBar.DeskChanging(settled, attached));

    // The wiring: Save itself asks, so a re-dock during the burst writes nothing.
    [Fact]
    public void A_save_while_another_desk_is_attached_writes_nothing() => StaThread.Run(() =>
    {
        var manager = Started();
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };
        // A desk no test machine has, so whatever is attached is another one.
        SettledLayout.SetValue(bar, "0,0,1,1|1,0,2,1");

        SaveMethod.Invoke(bar, null);

        Assert.Null(manager.State.FloatingBar);
        bar.Close();
    });

    // And the control: settled on the desk that is attached, the same call saves.
    [Fact]
    public void A_save_on_the_settled_desk_writes_the_spot() => StaThread.Run(() =>
    {
        var manager = Started();
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };
        SettledLayout.SetValue(bar, LayoutKeyMethod.Invoke(null, null));

        SaveMethod.Invoke(bar, null);

        Assert.NotNull(manager.State.FloatingBar);
        bar.Close();
    });

    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly FieldInfo SettledLayout = typeof(FloatingBar).GetField("settledLayout", Private)!;
    static readonly MethodInfo SaveMethod = typeof(FloatingBar).GetMethod("Save", Private, Type.EmptyTypes)!;
    static readonly MethodInfo LayoutKeyMethod =
        typeof(FloatingBar).GetMethod("LayoutKey", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)!;

    static WorkspaceManager Started()
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }
}
