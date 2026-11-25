using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace Deskbar.Windows.Recovery;

// WINDOW CLASS NAMES RUN OUT ON ONE PROCESSOR WHILE THE OTHERS HAVE THOUSANDS, and this moves some across
// (Deskbar task c5e9b17f, 2026-11-25).
//
// WHAT HAPPENED. Between 13:11 and 13:21 Deskbar could not start nine times in a row. Every start died of
//
//   System.ComponentModel.Win32Exception (8): Not enough memory resources are available to process this command.
//      at MS.Win32.HwndWrapper..ctor(...)
//
// and so did test hosts and Snipping Tool. It was not memory, not the desktop heap, and not a leak. The failing call was
// RegisterClassEx: WPF registers a window class with a name of its own for EVERY window it makes
// (HwndWrapper[Deskbar;;{guid}]), and each class name needs a slot in the session's table of class names, registered
// window messages and clipboard formats (the "USER atom table").
//
// WHAT WAS MEASURED, on Petre's 32-processor machine while it was happening:
//   * A thread pinned to one processor could register 300 classes or more on processors 0, 3, 10 and 27, and NONE on
//     thirteen others (4 to 7, 15, 16, 20 to 23, 25, 29, 31), the same numbers three rounds running. A thread that is
//     not pinned succeeds or fails by where the scheduler happens to put it: the "bursts" every earlier probe saw.
//   * Slots can be moved: 40 classes registered on processor 0 and unregistered on processor 4 left processor 4 able
//     to register 40, where it could register none before.
//   * Only about 850 names were in use (GetClipboardFormatName answers for every one), and the kernel held about
//     1,650 atom entries on the whole machine (pool tag AtmA). So nothing was leaking, although about 9,700 of the
//     16,320 slots could not be reached from any processor.
//
// WHY, read from ntoskrnl 10.0.26100 (RtlpInsertStringAtom, ExCreateHandleEx, ExDestroyHandle,
// ExpInsertLowLevelTableIntoFreeList). The table is a kernel handle table, and a name's number is 0xC000 plus its
// handle's index, so only indexes below 0x4000 can be names: 64 pages of 255. Free entries sit in one last-in-first-out
// list PER PROCESSOR. Taking one looks at the calling processor's list first and goes on to the next processor's only
// when a list is EMPTY; giving one back puts it on top of the list of the processor that gives it back; and only when
// every list is empty does the table grow by a page. So:
//   1. Once, the table really fills: 16,320 names in use at the same moment. (That day it was the window tests: a
//      test host kept every class of every test until it exited, 4,014 names for one run, measured, so four runs at
//      once fill the table, and several sessions run them. StaThread.Run in the tests gives them back since task
//      4888c75e.)
//   2. Windows grows the handle table past index 0x4000. An entry up there can never be a name: the kernel takes it,
//      sees the index, refuses with "not enough memory" and puts it straight back on top of the caller's list.
//   3. Those 255 entries stay in the lists for the rest of the session. A processor with one of them on top of its list
//      takes it, is refused and puts it back, every time; its list is not empty, so it never looks at its neighbours',
//      and whatever lies under that entry cannot be reached by anyone. Only signing out builds a new table.
//
// WHAT THIS DOES. Balance asks every processor, from a thread pinned to it, whether it can register Floor classes. One
// that cannot is given slots: they are taken on a processor that has plenty and released on the one that is short,
// which puts usable entries on top of the entry that blocked it. Measured on the broken session: 20 processors short,
// 2,368 slots moved in 66 ms, every processor able to register again, and it held.
//
// It is done BEFORE THE FIRST WINDOW of every start (Program.RunApp), because a start makes about thirty windows and
// was refused on whichever processor its thread ran on, and then every Keep interval and whenever a window is refused,
// because slots drift back: a name is taken where it is registered and returned where it is released.
//
// What it cannot do is remove the unusable entries or reach what lies under them; nothing in user mode can. It keeps
// usable ones on top of every list, which is the difference between a session that works and one that needs a reboot.
// On a table that never filled (every machine but a developer's, in practice) every processor passes the survey and
// nothing is moved.
public static class ClassNameSlots
{
    // A processor that cannot register this many classes is short. A start registers about thirty on the UI thread, and
    // other programs draw on the same list while it does.
    public const int Floor = 64;

    // What a short processor is topped up to, and what a processor that gives slots away keeps for itself.
    public const int Target = 128;

    // Every slot there is: 64 pages of 255 handle entries with an index a name can have (see the header).
    public const int Capacity = 64 * 255;

    // With this many names in use, something is filling the table right now, and the survey's own registrations (up to
    // Floor at a time, 2 x Target while moving) could be the ones that fill it, which is the one event that must not
    // happen. So a table this full is left alone.
    public const int BusyAt = Capacity - 2048;

    // How often Keep looks. A look that finds nothing short costs under twenty milliseconds of one thread on 32
    // processors (the survey, plus 2.5 ms to count the names in use). Thirty seconds, because slots drift fast when
    // programs come and go: five minutes after a repair that left every processor at least 68, one run of the window
    // tests had left six processors with none.
    public static readonly TimeSpan KeepEvery = TimeSpan.FromSeconds(30);

    // What a balance found and did. Short and StillShort are processor numbers (0-based, as Windows counts them).
    public sealed record Report(int Processors, IReadOnlyList<int> Short, int Moved, IReadOnlyList<int> StillShort, int InUse, bool Busy)
    {
        // Nothing was short, so nothing was touched.
        public bool Healthy => !Busy && Short.Count == 0;

        public override string ToString() =>
            Busy ? $"{InUse} of {Capacity} class names are in use, left alone"
            : Healthy ? $"all {Processors} processors can register window classes"
            : $"{Short.Count} of {Processors} processors could not register {Floor} window classes ({string.Join(",", Short)}); "
              + $"{Moved} slots moved onto them, {(StillShort.Count == 0 ? "all can now" : $"still short: {string.Join(",", StillShort)}")}";
    }

    // The machine as a balance sees it. The real one registers window classes; the tests' one is a model of the
    // kernel's lists with the rules in the header, because a healthy machine has nothing to balance.
    public interface IMachine
    {
        // How many processors this thread can be asked to run on, numbered from 0.
        int Processors { get; }

        // Moves the calling thread to that processor and keeps it there. False when it may not run there (a process
        // affinity that leaves the processor out), and such a processor is skipped.
        bool Pin(int processor);

        // One slot, taken on the processor the thread is pinned to, or None when Windows refuses.
        Maybe<nint> Take();

        // Gives a slot back, onto the list of the processor the thread is pinned to.
        void Release(nint slot);

        // How many names are in use in the session.
        int InUse();
    }

    // Surveys every processor and tops up the short ones. Must run on a thread nothing else uses: it leaves the thread
    // pinned to whichever processor it visited last.
    public static Report Balance(IMachine machine)
    {
        var inUse = machine.InUse();
        if (inUse >= BusyAt) return new Report(machine.Processors, [], 0, [], inUse, Busy: true);

        var before = Survey(machine);
        var shortOnes = before.Where(p => p.Have < Floor).ToList();
        if (shortOnes.Count == 0) return new Report(machine.Processors, [], 0, [], inUse, Busy: false);

        // Processors that passed the survey may give. How deep each one's list really is shows only when slots are
        // asked of it (TopUp), and one that turns out not to have enough is dropped from the list for the rest.
        var start = (Donors: (IReadOnlyList<int>)before.Where(p => p.Have >= Floor).Select(p => p.Processor).ToList(), Moved: 0);
        var end = shortOnes.Aggregate(start, (state, poor) =>
        {
            var topped = TopUp(machine, poor.Processor, Target - poor.Have, state.Donors);
            return (topped.Donors, state.Moved + topped.Moved);
        });

        var stillShort = Survey(machine).Where(p => p.Have < Floor).Select(p => p.Processor).ToList();
        return new Report(machine.Processors, shortOnes.Select(p => p.Processor).ToList(), end.Moved, stillShort, inUse, Busy: false);
    }

    // How many classes each processor can register, counted up to Floor, with every list left as it was. A processor the
    // thread cannot be pinned to is left out.
    static IReadOnlyList<(int Processor, int Have)> Survey(IMachine machine) =>
        Enumerable.Range(0, machine.Processors)
            .Where(machine.Pin)
            .Select(processor =>
            {
                var taken = Take(machine, Floor);
                PutBack(machine, taken);
                return (processor, taken.Count);
            })
            .ToList();

    // Gives the short processor `need` slots from the first donor that can spare them, and says which donors are still
    // worth asking. A donor is asked for what is needed PLUS Target and gets Target straight back, so it is never left
    // short itself; one that cannot hand over that many gets everything back and is not asked again.
    static (IReadOnlyList<int> Donors, int Moved) TopUp(IMachine machine, int poor, int need, IReadOnlyList<int> donors)
    {
        if (donors.Count == 0) return (donors, 0);
        var rest = donors.Skip(1).ToList();
        if (!machine.Pin(donors[0])) return TopUp(machine, poor, need, rest);

        var taken = Take(machine, need + Target);
        if (taken.Count < need + Target)
        {
            PutBack(machine, taken);
            return TopUp(machine, poor, need, rest);
        }

        PutBack(machine, taken.Skip(need).ToList());
        // The short processor was pinned to a moment ago in the survey, so this pin holds; if it ever did not, the slots
        // are released where the thread is, which loses nothing.
        machine.Pin(poor);
        taken.Take(need).ToList().ForEach(machine.Release);
        return (donors, need);
    }

    // Up to `count` slots from the processor the thread is pinned to, stopping at the first refusal: after one, the
    // unusable entry is on top of the list and every further try would be refused the same way.
    static IReadOnlyList<nint> Take(IMachine machine, int count) =>
        Enumerable.Range(0, count)
            .Select(_ => machine.Take())
            .TakeWhile(slot => slot.HasValue)
            .Select(slot => slot.Value)
            .ToList();

    // Gives slots back in the opposite order to the one they were taken in. The lists are last in, first out, so this
    // leaves the list exactly as it was: the same slots, in the same order.
    static void PutBack(IMachine machine, IReadOnlyList<nint> slots) =>
        slots.Reverse().ToList().ForEach(machine.Release);

    // The same on the real machine, on a thread of its own that ends with the call: pinning is per thread, and the
    // caller's thread (the UI thread, at the start) must stay free to run anywhere. A failure comes back as a Result,
    // because this runs before the first window of every start and must never be what stops one.
    public static Result<Report> Balance() =>
        Result.Try(() =>
        {
            Report? report = null;
            Exception? fault = null;
            var thread = new Thread(() =>
            {
                try { report = Balance(new WindowsMachine()); }
                catch (Exception e) { fault = e; }
            }) { IsBackground = true, Name = "Deskbar class name slots" };
            thread.Start();
            thread.Join();
            return report ?? throw new InvalidOperationException(fault?.Message ?? "the balance did not run", fault);
        }, e => $"class name slots not balanced: {e.Message}");

    // Balances every KeepEvery, and soon after any BalanceSoon. One balance at a time, on a thread of its own, never
    // the caller's. `report` hears every outcome, on a timer thread; disposing stops it.
    public static IDisposable Keep(Action<Result<Report>> report) =>
        Observable.Interval(KeepEvery).Select(_ => Unit.Default)
            // A refusal seldom comes alone (a rebuild asks for several ring frames), so a burst is answered once, a
            // second after its last one.
            .Merge(Asked.Throttle(TimeSpan.FromSeconds(1)))
            .Synchronize()
            .Subscribe(_ => report(Balance()));

    // Says a window was just refused, from wherever the app caught it: the keeper balances within a second or two
    // instead of at its next interval, so what was refused can be made on the next try. Does nothing while no keeper
    // runs (a test, a `ctl` client).
    public static void BalanceSoon() => Asked.OnNext(Unit.Default);

    // Synchronized, because the places that catch a refusal are not all on one thread.
    static readonly System.Reactive.Subjects.ISubject<Unit> Asked = System.Reactive.Subjects.Subject.Synchronize(new System.Reactive.Subjects.Subject<Unit>());

    // The real machine: a slot is a registered window class with a name nothing else uses, and it is taken and given
    // back on whichever processor the thread is pinned to.
    sealed class WindowsMachine : IMachine
    {
        // Every logical processor as (group, number). Windows puts at most 64 in a group; a machine with more has
        // several, and a thread is pinned to one processor of one group.
        readonly IReadOnlyList<(ushort Group, byte Number)> processors =
            Enumerable.Range(0, GetActiveProcessorGroupCount())
                .SelectMany(group => Enumerable.Range(0, (int)GetActiveProcessorCount((ushort)group)).Select(number => ((ushort)group, (byte)number)))
                .ToList();

        readonly nint instance = GetModuleHandleW(0);

        // user32's own DefWindowProc as the classes' window procedure: no window is ever made of them, and a native
        // address needs no delegate kept alive. Looked up once for the process, since a machine is made per balance.
        static readonly nint WindowProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");

        readonly string tag = Guid.NewGuid().ToString("N");
        int serial;

        public int Processors => processors.Count;

        public bool Pin(int processor)
        {
            var (group, number) = processors[processor];
            var affinity = new GROUP_AFFINITY { Mask = (nuint)1 << number, Group = group };
            if (!SetThreadGroupAffinity(GetCurrentThread(), ref affinity, 0)) return false;
            // Windows moves the thread before the call returns; the yields are for the case it has not yet.
            return Enumerable.Range(0, 1000).Any(_ => Arrived(group, number));
        }

        // Whether the thread runs on that processor now; gives the rest of its turn away when it does not.
        static bool Arrived(ushort group, byte number)
        {
            if (IsOn(group, number)) return true;
            Thread.Yield();
            return false;
        }

        static bool IsOn(ushort group, byte number)
        {
            GetCurrentProcessorNumberEx(out var at);
            return at.Group == group && at.Number == number;
        }

        public Maybe<nint> Take()
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = WindowProc,
                hInstance = instance,
                lpszClassName = $"Deskbar.ClassNameSlot[{tag};{serial++}]",
            };
            var atom = RegisterClassExW(ref windowClass);
            return atom == 0 ? Maybe<nint>.None : (nint)atom;
        }

        public void Release(nint slot) => UnregisterClassW(slot, instance);

        // GetClipboardFormatName answers for every name in the table, class names included (Raymond Chen, "How can I
        // investigate the possibility of a lot of leaked window classes", 2017-10-06), and for nothing else.
        public int InUse()
        {
            var buffer = new char[4];
            return Enumerable.Range(0xC000, 0x4000).Count(atom => GetClipboardFormatNameW((uint)atom, buffer, buffer.Length) > 0);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public int cbSize;
            public int style;
            public nint lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public nint hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GROUP_AFFINITY
        {
            public nuint Mask;
            public ushort Group;
            public ushort Reserved0, Reserved1, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESSOR_NUMBER
        {
            public ushort Group;
            public byte Number;
            public byte Reserved;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterClassW(nint classAtom, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClipboardFormatNameW(uint format, [Out] char[] name, int max);

        [DllImport("kernel32.dll")]
        static extern nint GetModuleHandleW(nint name);

        [DllImport("kernel32.dll")]
        static extern nint GetCurrentThread();

        [DllImport("kernel32.dll")]
        static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll")]
        static extern uint GetActiveProcessorCount(ushort group);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetThreadGroupAffinity(nint thread, ref GROUP_AFFINITY affinity, nint previous);

        [DllImport("kernel32.dll")]
        static extern void GetCurrentProcessorNumberEx(out PROCESSOR_NUMBER at);
    }
}
