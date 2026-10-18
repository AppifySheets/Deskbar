using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;

namespace TaskSpaces.Windows.Audio;

// WHICH PROCESSES ARE MAKING SOUND, for the bar's sound mark. Petre: "if any window is playing sound, show it
// on it so i can quickly see what's the source of sound", then "c" from three mockups: a soft pulsing ring.
//
// Measured on his machine before this was written, with the default playback device's audio sessions:
//
//   pid=42996 msedge parent=67320 state=active peak=0.345
//
// So a session names a PROCESS, and for a browser it is not the process that owns any window: Chromium
// plays every tab's sound through one audio helper, a child of the browser's main process. Which is why this
// reports each playing process together with its same-exe ancestors (42996 and 67320 above), and the bar
// decides which of that browser's windows it was (see FloatingBar.Activity).
public interface IAudioActivity
{
    // Every process with a playing session, plus its ancestors that run the same exe.
    IReadOnlySet<int> Playing { get; }

    // Raised (on a pool thread) whenever that set changes.
    IObservable<Unit> Changed { get; }
}

// Polled every 700 ms, because Windows only reports a session's state (active as soon as a stream is open,
// paused video included), never "sound is coming out"; the peak meter answers that. A process counts as
// playing while its peak was above silence in any of the last three polls, so the gaps between notes do not
// make the mark flicker.
public sealed class AudioActivity : IAudioActivity, IDisposable
{
    const float Silence = 0.0005f;
    const int HoldPolls = 3;

    readonly Subject<Unit> changed = new();
    readonly IDisposable poll;
    readonly Action<string>? trace;
    string? lastFailure;
    readonly Dictionary<int, int> heardAgo = [];
    IReadOnlySet<int> playing = new HashSet<int>();

    // `trace` hears every change of the playing set and each new kind of failure, once.
    public AudioActivity(Action<string>? trace = null)
    {
        this.trace = trace;
        poll = Observable.Interval(TimeSpan.FromMilliseconds(700)).Subscribe(_ => Poll());
    }

    public IReadOnlySet<int> Playing => playing;
    public IObservable<Unit> Changed => changed.AsObservable();

    void Poll()
    {
        try
        {
            var loud = SessionsAbove(Silence);
            heardAgo.Keys.ToList().ForEach(pid => heardAgo[pid]++);
            loud.ToList().ForEach(pid => heardAgo[pid] = 0);
            heardAgo.Where(p => p.Value >= HoldPolls).Select(p => p.Key).ToList().ForEach(pid => heardAgo.Remove(pid));

            var now = WithSameExeAncestors(heardAgo.Keys);
            lastFailure = null;
            if (now.SetEquals(playing)) return;
            trace?.Invoke($"audio: playing sessions=[{string.Join(",", loud)}] with ancestors=[{string.Join(",", now)}]");
            playing = now;
            changed.OnNext(Unit.Default);
        }
        catch (Exception e)
        {
            // A poll that fails (the audio service restarting, a device going away) is one missed reading,
            // not a reason to stop polling. Traced once per kind, so a failure that repeats every poll is one line.
            var failure = $"{e.GetType().Name}: {e.Message}";
            if (failure != lastFailure) trace?.Invoke($"audio: poll failed {failure}");
            lastFailure = failure;
        }
    }

    // The session PIDs, with each one's chain of parents for as long as the parent runs the same exe: a
    // browser's audio helper leads to the browser itself, which is the process that owns its windows.
    static HashSet<int> WithSameExeAncestors(IEnumerable<int> pids)
    {
        var processes = ProcessTable();
        var result = new HashSet<int>();
        pids.ToList().ForEach(pid =>
        {
            var at = pid;
            for (var hops = 0; hops < 8 && result.Add(at); hops++)
            {
                if (!processes.TryGetValue(at, out var me) || !processes.TryGetValue(me.Parent, out var parent)) break;
                if (!string.Equals(me.Exe, parent.Exe, StringComparison.OrdinalIgnoreCase)) break;
                at = me.Parent;
            }
        });
        return result;
    }

    // pid -> (parent pid, exe name), from one Toolhelp snapshot.
    static Dictionary<int, (int Parent, string Exe)> ProcessTable()
    {
        var table = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == -1) return table;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                table[entry.ProcessId] = (entry.ParentProcessId, entry.ExeFile);
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return table;
    }

    // The default playback device's sessions whose peak is above `threshold`, by process id.
    static IReadOnlyList<int> SessionsAbove(float threshold)
    {
        var found = new List<int>();
        var enumerator = (IMMDeviceEnumerator)CoreAudio.CreateDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0 || device is null) return found;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 0x17, 0, out var activated) != 0 || activated is not IAudioSessionManager2 manager) return found;
            if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) return found;
            sessions.GetCount(out var count);
            Enumerable.Range(0, count).ToList().ForEach(i =>
            {
                if (sessions.GetSession(i, out var session) != 0 || session is null) return;
                try
                {
                    if (session.GetState(out var state) == 0 && state == 1 /* active */
                        && session is IAudioMeterInformation meter && meter.GetPeakValue(out var peak) == 0 && peak > threshold
                        && session.GetProcessId(out var pid) == 0 && pid != 0)
                        found.Add((int)pid);
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            });
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return found;
    }

    public void Dispose()
    {
        poll.Dispose();
        changed.OnCompleted();
    }

    // --- Core Audio, the slots used and every slot before them, in vtable order -----------------------------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint group, int flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint group, int flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2? session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notifications);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public int ModuleId, Threads, ParentProcessId, PriorityClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
