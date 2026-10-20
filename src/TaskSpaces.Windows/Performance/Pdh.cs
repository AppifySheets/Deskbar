using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Performance;

// Windows' performance counters through pdh.dll, by hand. The System.Diagnostics.PerformanceCounter package
// would do the same through the registry's localised counter names; PdhAddEnglishCounter takes the English
// path on any language of Windows, which is the one thing a counter path written in source needs.
//
// One query holds every counter and one PdhCollectQueryData samples them all at once. Rate counters (CPU
// utility, disk idle time, GPU engine utilisation) are differences between two collections, so the first
// reading after adding a counter has no value yet: the caller collects once up front and then once a tick.
internal sealed class Pdh : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    // Without it PDH clips at 100, and "% Processor Utility" legitimately passes 100 on a boosting CPU.
    // The caller clamps instead, after summing, so a sum of clipped parts never hides real load.
    const uint PDH_FMT_NOCAP100 = 0x00008000;
    const uint Format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

    readonly nint query;

    Pdh(nint query) => this.query = query;

    public static Result<Pdh> Open() =>
        PdhOpenQueryW(null, 0, out var query) == 0 ? new Pdh(query) : Result.Failure<Pdh>("PdhOpenQuery failed");

    // A counter by its English path, for example @"\PhysicalDisk(*)\% Idle Time". A path this Windows does not
    // have (no GPU counters on a VM without a GPU, say) is an expected absence, not an error.
    public Maybe<nint> Add(string path) =>
        PdhAddEnglishCounterW(query, path, 0, out var counter) == 0 ? counter : Maybe<nint>.None;

    public bool Collect() => PdhCollectQueryData(query) == 0;

    // A single-instance counter's value, or None while it has nothing to say (the first collection of a rate).
    public static Maybe<double> Value(nint counter) =>
        PdhGetFormattedCounterValue(counter, Format, out _, out var value) == 0 && Valid(value.CStatus)
            ? value.DoubleValue
            : Maybe<double>.None;

    // A wildcard counter's values, one per instance, by instance name. Empty when there are none or the
    // counter has no reading yet.
    public static IReadOnlyList<(string Instance, double Value)> Values(nint counter)
    {
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, Format, ref size, ref count, 0) != PDH_MORE_DATA || size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, Format, ref size, ref count, buffer) != 0) return [];
            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: the name pointer, then the 16 byte value (status, padding,
            // the double at offset 8 within it), 24 bytes in all. The names live after the items in the
            // same buffer, so they are read before it is freed.
            const int itemSize = 24;
            return Enumerable.Range(0, (int)count)
                .Select(i => buffer + i * itemSize)
                .Select(item => (Name: Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "",
                                 Status: (uint)Marshal.ReadInt32(item, 8),
                                 Value: BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))))
                .Where(x => Valid(x.Status))
                .Select(x => (x.Name, x.Value))
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static bool Valid(uint status) => status is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA;

    public void Dispose() => PdhCloseQuery(query);

    [StructLayout(LayoutKind.Explicit)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(nint query);
}
