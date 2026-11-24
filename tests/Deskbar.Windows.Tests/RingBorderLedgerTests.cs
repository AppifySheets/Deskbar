using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The ledger of window borders a Deskbar copy has coloured (RingBorderLedger). It is how the next copy puts back a border
// that a killed or crashed copy left on a window: "two windows have a solid line", one of them the focused window's
// ring, the other a border nobody owned any more.
public class RingBorderLedgerTests
{
    [Fact]
    public void What_is_written_reads_back_the_same()
    {
        var entries = new[] { new RingBorderLedger.Entry(0x9DA1022, 4321), new RingBorderLedger.Entry(0xE522C2, 77) };
        Assert.Equal(entries, RingBorderLedger.Parse(RingBorderLedger.Format(entries)));
    }

    [Fact]
    public void Nothing_written_reads_back_as_nothing() =>
        Assert.Empty(RingBorderLedger.Parse(RingBorderLedger.Format([])));

    // A ledger cut short by a kill mid-write, or edited by hand: the lines that read are kept, the rest skipped, and
    // nothing throws, because a start must never fail over a leftover border.
    [Fact]
    public void A_damaged_line_is_skipped_and_the_rest_kept()
    {
        var parsed = RingBorderLedger.Parse("0x9DA1022 4321\nnonsense\n0xE522C2\n0xZZ 5\n\n0x808D0 12\n");
        Assert.Equal([new RingBorderLedger.Entry(0x9DA1022, 4321), new RingBorderLedger.Entry(0x808D0, 12)], parsed);
    }

    // Put back only on a window that is still there AND still belongs to the process it did when the border was set. A
    // window handle is reused once its window closes, and a border reset on someone else's window would undo a colour
    // that program chose (Windows Terminal sets its own).
    [Fact]
    public void Only_a_live_window_of_the_same_process_is_put_back()
    {
        var entries = new[]
        {
            new RingBorderLedger.Entry(1, 100), // still there, same process: put back
            new RingBorderLedger.Entry(2, 200), // closed since
            new RingBorderLedger.Entry(3, 300), // handle reused by another process
        };
        var live = new Dictionary<nint, uint> { [1] = 100, [3] = 999 };

        var leftovers = RingBorderLedger.Leftovers(entries, isWindow: live.ContainsKey, processOf: w => live[w]);

        Assert.Equal([(nint)1], leftovers);
    }
}
