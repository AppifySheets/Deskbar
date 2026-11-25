using CSharpFunctionalExtensions;
using Deskbar.Windows.Recovery;

namespace Deskbar.Windows.Tests;

// Task c5e9b17f: on 2026-11-25 Deskbar could not start nine times in ten minutes, each time refused a window class on
// whichever processor its thread ran on, while other processors had thousands of free class name slots.
// ClassNameSlots' header has the measurements and the kernel's rules. A healthy machine has nothing to balance, so
// these run the balance against a model of the kernel's free lists that follows those rules (Lists, below), set up the
// way the broken session was. One test at the end runs it on the real machine.
public class ClassNameSlotsTests
{
    const int Floor = ClassNameSlots.Floor;
    const int Target = ClassNameSlots.Target;

    // The broken session in small: eight processors, an unusable entry at the bottom of every list, and all the usable
    // slots on the first two.
    static Lists Broken() => new(
        Enumerable.Range(0, 8).Select(processor => processor < 2 ? Good(processor, 600).Append(Lists.Unusable + processor) : [Lists.Unusable + processor]));

    // `count` usable slots with numbers nothing else has: 1000 x processor and up.
    static IEnumerable<int> Good(int processor, int count) => Enumerable.Range(1000 * (processor + 1), count);

    [Fact]
    public void A_blocked_processor_fails_in_the_model_as_it_did_on_the_machine()
    {
        var lists = Broken();
        lists.Pin(5);
        Assert.True(lists.Take().HasNoValue);
        // Its list is not empty, so it never looks at the 600 slots two lists further on, however often it asks.
        Assert.True(Enumerable.Range(0, 50).All(_ => lists.Take().HasNoValue));
        lists.Pin(0);
        Assert.True(lists.Take().HasValue);
    }

    [Fact]
    public void Every_blocked_processor_is_given_slots()
    {
        var lists = Broken();

        var report = ClassNameSlots.Balance(lists);

        Assert.Equal([2, 3, 4, 5, 6, 7], report.Short);
        Assert.Equal(6 * Target, report.Moved);
        Assert.Empty(report.StillShort);
        Assert.False(report.Healthy);
        Assert.All(Enumerable.Range(0, 8), processor => Assert.True(lists.CanTake(processor) >= Target, $"processor {processor} can take {lists.CanTake(processor)}"));
    }

    [Fact]
    public void The_processors_that_give_keep_enough_for_themselves()
    {
        var lists = Broken();

        ClassNameSlots.Balance(lists);

        // Each is asked for Target more than it gives, so the first stops giving while it still has more than Target and
        // the second takes over. Between them they are down by exactly what the six short ones got.
        Assert.True(lists.CanTake(0) >= Target);
        Assert.True(lists.CanTake(1) >= Target);
        Assert.Equal(2 * 600 - 6 * Target, lists.CanTake(0) + lists.CanTake(1));
    }

    // A processor is asked for what is needed plus Target. One that cannot hand that over is put back as it was and the
    // next one is asked.
    [Fact]
    public void A_processor_that_cannot_spare_enough_gives_nothing()
    {
        var lists = new Lists([
            Good(0, 2 * Target - 1).Append(Lists.Unusable),
            Good(1, 600).Append(Lists.Unusable + 1),
            [Lists.Unusable + 2],
        ]);
        var first = lists.Of(0);

        var report = ClassNameSlots.Balance(lists);

        Assert.Equal([2], report.Short);
        Assert.Equal(Target, report.Moved);
        Assert.Equal(first, lists.Of(0));
        Assert.Equal(600 - Target, lists.CanTake(1));
    }

    // The survey takes slots and gives them back in reverse, and the lists are last in, first out, so a list nothing was
    // moved to or from comes out in the order it went in.
    [Fact]
    public void Lists_that_need_nothing_are_left_in_the_order_they_were()
    {
        var lists = new Lists([Good(0, 200).Append(Lists.Unusable), Good(1, 300).Append(Lists.Unusable + 1)]);
        var (first, second) = (lists.Of(0), lists.Of(1));

        var report = ClassNameSlots.Balance(lists);

        Assert.True(report.Healthy);
        Assert.Equal(first, lists.Of(0));
        Assert.Equal(second, lists.Of(1));
    }

    [Fact]
    public void When_nobody_can_spare_slots_it_says_who_is_still_short()
    {
        var lists = new Lists([Good(0, Floor + 10).Append(Lists.Unusable), [Lists.Unusable + 1], [Lists.Unusable + 2]]);

        var report = ClassNameSlots.Balance(lists);

        Assert.Equal([1, 2], report.Short);
        Assert.Equal(0, report.Moved);
        Assert.Equal([1, 2], report.StillShort);
        Assert.Equal(Floor + 10, lists.CanTake(0));
    }

    // A table that never filled has no unusable entries. A processor with an empty list takes from the next one, so
    // nobody is short, and the balance moves nothing.
    [Fact]
    public void A_table_that_never_filled_is_healthy()
    {
        var lists = new Lists([Good(0, 500), [], [], []]);

        var report = ClassNameSlots.Balance(lists);

        Assert.True(report.Healthy);
        Assert.Equal(0, report.Moved);
        Assert.Equal(500, Enumerable.Range(0, 4).Sum(processor => lists.Of(processor).Count));
        Assert.False(lists.Grew);
    }

    // The one event that breaks a table is filling it. With the table nearly full, the survey's own registrations could
    // be what fills it, so it does not take part.
    [Fact]
    public void A_table_that_is_nearly_full_is_left_alone()
    {
        var lists = new Lists([Good(0, 40)]) { NamesInUse = ClassNameSlots.BusyAt };

        var report = ClassNameSlots.Balance(lists);

        Assert.True(report.Busy);
        Assert.False(report.Healthy);
        Assert.Equal(0, lists.Taken);
        Assert.False(lists.Grew);
    }

    [Fact]
    public void A_processor_the_thread_may_not_run_on_is_skipped()
    {
        var lists = new Lists([Good(0, 600).Append(Lists.Unusable), [Lists.Unusable + 1], [Lists.Unusable + 2]]) { Barred = [1] };

        var report = ClassNameSlots.Balance(lists);

        Assert.Equal([2], report.Short);
        Assert.Empty(report.StillShort);
        Assert.Equal([Lists.Unusable + 1], lists.Of(1));
    }

    [Fact]
    public void The_report_reads_as_a_sentence()
    {
        Assert.Equal("all 8 processors can register window classes", new ClassNameSlots.Report(8, [], 0, [], 800, Busy: false).ToString());
        Assert.Equal($"2 of 8 processors could not register {Floor} window classes (4,5); 256 slots moved onto them, all can now",
            new ClassNameSlots.Report(8, [4, 5], 256, [], 800, Busy: false).ToString());
        Assert.Equal($"2 of 8 processors could not register {Floor} window classes (4,5); 128 slots moved onto them, still short: 5",
            new ClassNameSlots.Report(8, [4, 5], 128, [5], 800, Busy: false).ToString());
        Assert.Equal($"15000 of {ClassNameSlots.Capacity} class names are in use, left alone", new ClassNameSlots.Report(8, [], 0, [], 15000, Busy: true).ToString());
    }

    // The real machine: registers and unregisters real window classes on every processor from a thread of its own. No
    // window is made. On a healthy machine nothing is short; on a broken one it repairs it, and either is a pass.
    [Fact]
    public void The_real_machine_can_be_balanced()
    {
        var report = ClassNameSlots.Balance();

        Assert.True(report.IsSuccess, report.IsFailure ? report.Error : "");
        Assert.True(report.Value.Processors > 0);
        Assert.True(report.Value.Busy || report.Value.StillShort.Count <= report.Value.Short.Count);
    }

    // THE KERNEL'S FREE LISTS, as ntoskrnl keeps them for the table of class names (ClassNameSlots' header has where
    // each rule was read and what was measured):
    //   * one list per processor, last in, first out (index 0 here is the top);
    //   * taking looks at the caller's own list first and at the following ones only while lists are EMPTY;
    //   * an entry at or above Unusable cannot be a name: it is refused and put on top of the CALLER's list;
    //   * giving back puts the entry on top of the caller's list;
    //   * only when every list is empty does the table grow, by a page of entries at the bottom of the caller's list.
    sealed class Lists(IEnumerable<IEnumerable<int>> lists) : ClassNameSlots.IMachine
    {
        // Entries from this number up have an index too high to be a name.
        public const int Unusable = 0x4000;

        readonly List<List<int>> lists = lists.Select(list => list.ToList()).ToList();
        int at;
        int nextPage = Unusable + 1000;

        // Names in use besides the ones the balance holds.
        public int NamesInUse { get; init; } = 800;

        // Processors the thread may not be pinned to.
        public IReadOnlyList<int> Barred { get; init; } = [];

        // How many takes were asked for, and whether any of them made the table grow.
        public int Taken { get; private set; }
        public bool Grew { get; private set; }

        public int Processors => lists.Count;

        public bool Pin(int processor)
        {
            if (Barred.Contains(processor)) return false;
            at = processor;
            return true;
        }

        public Maybe<nint> Take()
        {
            Taken++;
            var from = Enumerable.Range(0, lists.Count).Select(step => (at + step) % lists.Count).Where(list => lists[list].Count > 0).Take(1).ToList();
            if (from.Count == 0)
            {
                // Every list is empty: the table grows. Past the last usable page the new entries are no use.
                Grew = true;
                lists[at].AddRange(Enumerable.Range(nextPage, 255));
                nextPage += 255;
                return Take();
            }

            var entry = lists[from[0]][0];
            lists[from[0]].RemoveAt(0);
            if (entry < Unusable) return (nint)entry;
            lists[at].Insert(0, entry);
            return Maybe<nint>.None;
        }

        public void Release(nint slot) => lists[at].Insert(0, (int)slot);

        public int InUse() => NamesInUse;

        // A list, top first.
        public IReadOnlyList<int> Of(int processor) => lists[processor].ToList();

        // How many slots a thread on that processor would get before its first refusal, counted on a copy.
        public int CanTake(int processor)
        {
            var copy = new Lists(lists) { Barred = Barred };
            copy.Pin(processor);
            return Enumerable.Range(0, 100_000).TakeWhile(_ => copy.Take().HasValue).Count();
        }
    }
}
