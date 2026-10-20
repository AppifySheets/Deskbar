using System.Reactive.Linq;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Performance;

// THE MACHINE'S LOAD, for the bar's stats line (Petre: "let's show system stats, cpu, memory, disk, gpu below,
// above the music player", then "b" from three mockups: a sparkline each, "and clicking opens task manager").
//
// Percentages are 0..100 and read the way Task Manager's Performance tab reads them, so the bar and the tab
// it opens agree:
//   CPU     \Processor Information(_Total)\% Processor Utility, Task Manager's own counter since Windows 8.
//           It passes 100 on a boosting CPU and is clamped. Falls back to % Processor Time where it is missing.
//   Memory  in use over installed, from GlobalMemoryStatusEx.
//   Disk    ACTIVE TIME, 100 minus % Idle Time, of the busiest physical disk. Task Manager shows one graph per
//           disk; the bar has room for one, and the busiest is the one that explains a slow machine.
//   GPU     Task Manager's reading: each engine's utilisation summed over the processes using it, and the
//           busiest engine of the busiest GPU. None on a machine without the GPU counters.
// The system drive's free space rides along for the tooltip, as the one disk number that is not a rate.
public sealed record SystemSample(
    double Cpu,
    double Memory,
    ulong MemoryUsed,
    ulong MemoryTotal,
    double Disk,
    Maybe<double> Gpu,
    long SystemDriveFree,
    long SystemDriveSize);

// What the bar needs: an interface, so the tests hand it a fake and never depend on the load of whoever runs them.
public interface ISystemStats
{
    // One sample a second, raised on a pool thread, so a UI subscriber marshals it itself.
    IObservable<SystemSample> Samples { get; }
}

public sealed class SystemStats : ISystemStats, IDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    readonly Lock gate = new();
    readonly Maybe<Pdh> pdh;
    readonly Maybe<nint> cpu, disk, gpu;
    bool disposed;

    public SystemStats()
    {
        pdh = Pdh.Open().AsMaybe();
        cpu = pdh.Bind(p => p.Add(@"\Processor Information(_Total)\% Processor Utility")
            .Or(() => p.Add(@"\Processor(_Total)\% Processor Time")));
        disk = pdh.Bind(p => p.Add(@"\PhysicalDisk(*)\% Idle Time"));
        gpu = pdh.Bind(p => p.Add(@"\GPU Engine(*)\Utilization Percentage"));
        // The first collection, so the first tick's rates have something to be the difference from.
        pdh.Execute(p => p.Collect());

        // Interval serialises its ticks, so Read never runs twice at once; Publish and RefCount share one
        // timer between subscribers and stop it when the last one leaves.
        Samples = Observable.Interval(Interval)
            .Select(_ => Read())
            .Where(sample => sample.HasValue)
            .Select(sample => sample.Value)
            .Publish()
            .RefCount();
    }

    public IObservable<SystemSample> Samples { get; }

    Maybe<SystemSample> Read()
    {
        lock (gate)
        {
            if (disposed || pdh.HasNoValue || !pdh.Value.Collect()) return Maybe<SystemSample>.None;
            var (used, total) = Memory();
            var (free, size) = SystemDrive();
            return new SystemSample(
                Cpu: StatsMath.Percent(cpu.Bind(Pdh.Value).GetValueOrDefault()),
                Memory: total == 0 ? 0 : StatsMath.Percent(100.0 * used / total),
                MemoryUsed: used,
                MemoryTotal: total,
                Disk: disk.Map(Pdh.Values).Map(StatsMath.BusiestDisk).GetValueOrDefault(),
                Gpu: gpu.Map(Pdh.Values).Bind(StatsMath.BusiestGpuEngine),
                SystemDriveFree: free,
                SystemDriveSize: size);
        }
    }

    static (ulong Used, ulong Total) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys - status.ullAvailPhys, status.ullTotalPhys) : (0, 0);
    }

    static (long Free, long Size) SystemDrive()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pdh.Execute(p => p.Dispose());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

// The arithmetic on raw counter readings, apart from PDH so it can be tested with made-up instances.
internal static class StatsMath
{
    public static double Percent(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);

    // Active time of the busiest physical disk, from each disk's % Idle Time. "_Total" is the AVERAGE over
    // disks, which would show a saturated second disk as half busy, so it is left out.
    public static double BusiestDisk(IReadOnlyList<(string Instance, double Value)> idle) =>
        idle.Where(d => d.Instance != "_Total")
            .Select(d => Percent(100 - d.Value))
            .DefaultIfEmpty(0)
            .Max();

    // Task Manager's GPU percentage. Instances read
    // "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_3D": one per process per engine. An engine's
    // load is the sum over the processes using it; the GPU's is its busiest engine; and with two GPUs, the
    // busier one. None when there are no instances at all, so the bar can say "no GPU" rather than "0%".
    public static Maybe<double> BusiestGpuEngine(IReadOnlyList<(string Instance, double Value)> engines) =>
        engines.Count == 0
            ? Maybe<double>.None
            : engines.GroupBy(e => EngineOf(e.Instance))
                .Select(engine => Percent(engine.Sum(e => e.Value)))
                .Max();

    // The part of an instance name that names the engine, from "luid_" up to "_engtype": the adapter, the
    // physical GPU on it and the engine's index. The process id before it is what the sum folds away.
    internal static string EngineOf(string instance)
    {
        var start = instance.IndexOf("luid_", StringComparison.Ordinal);
        var end = instance.IndexOf("_engtype", StringComparison.Ordinal);
        return start >= 0 && end > start ? instance[start..end] : instance;
    }
}
