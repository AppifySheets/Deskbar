using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace Deskbar.Windows.Audio;

// The master volume of the default playback device, for the bar's volume dial (Petre: "add volume as a
// scrollable wheel, with a knob increasing and decreasing as i hover my mouse on top of it, looking like a
// progress bar until i hover over it", then "c" from three mockups: a ring around the speaker).
//
// Level is 0..1, the same scalar Windows' own volume flyout shows as 0..100.
public sealed record VolumeLevel(double Level, bool Muted);

// What the bar needs, and all it gets: an interface so the tests hand it a fake. A test that drove the
// real one would change the volume of whoever runs the suite.
public interface IVolumeControl
{
    // The volume now, or None when there is no playback device at all (a machine with no sound card, or
    // every output disabled), in which case the bar hides the dial rather than drawing a dead one.
    Maybe<VolumeLevel> Current { get; }

    // Every change made ELSEWHERE: the keyboard's volume keys, Windows' flyout, an app, a new default device.
    // Not the changes made through this interface, whose caller already knows the new level; echoing those
    // back would make a fast wheel flicker as each step's notification arrives after the next step.
    // Raised on whatever thread Windows calls back on, so a UI subscriber marshals it itself.
    IObservable<VolumeLevel> Changes { get; }

    Result SetLevel(double level);
    Result SetMuted(bool muted);
}

// Core Audio, by hand: IMMDeviceEnumerator -> default render endpoint -> IAudioEndpointVolume. A NuGet
// wrapper (NAudio) would bring a whole audio stack for five calls.
//
// Follows the DEFAULT device: plugging in a headset makes Windows switch, and the dial has to follow it or
// it would be turning a speaker nobody is listening to. IMMNotificationClient says when, and the endpoint is
// bound again.
public sealed class SystemVolume : IVolumeControl, IDisposable
{
    readonly Lock gate = new();
    readonly Subject<VolumeLevel> changes = new();
    readonly IMMDeviceEnumerator? enumerator;
    readonly DeviceWatcher watcher;
    readonly VolumeCallback callback;
    IAudioEndpointVolume? endpoint;

    // Our own event context. Every Set call passes it, and Windows hands it back in the notification that
    // change causes, which is how Changes leaves out the changes made through this class.
    static Guid context = Guid.NewGuid();

    public SystemVolume()
    {
        callback = new VolumeCallback((source, level) =>
        {
            if (source != context) changes.OnNext(level);
        });
        watcher = new DeviceWatcher(Rebind);
        try
        {
            enumerator = (IMMDeviceEnumerator)CoreAudio.CreateDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(watcher);
        }
        catch (COMException)
        {
            // No audio service at all. Current answers None and the dial stays hidden.
            enumerator = null;
        }
        Rebind();
    }

    public IObservable<VolumeLevel> Changes => changes.AsObservable();

    public Maybe<VolumeLevel> Current
    {
        get
        {
            lock (gate)
            {
                if (endpoint is null) return Maybe<VolumeLevel>.None;
                return endpoint.GetMasterVolumeLevelScalar(out var level) == 0 && endpoint.GetMute(out var muted) == 0
                    ? new VolumeLevel(level, muted)
                    : Maybe<VolumeLevel>.None;
            }
        }
    }

    public Result SetLevel(double level)
    {
        lock (gate)
            return endpoint is null
                ? Result.Failure("No playback device")
                : Check(endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref context), "set the volume");
    }

    public Result SetMuted(bool muted)
    {
        lock (gate)
            return endpoint is null
                ? Result.Failure("No playback device")
                : Check(endpoint.SetMute(muted, ref context), muted ? "mute" : "unmute");
    }

    static Result Check(int hresult, string what) =>
        hresult == 0 ? Result.Success() : Result.Failure($"Could not {what} (0x{hresult:X8})");

    // Drops the old endpoint's callback and takes the current default's, then reports its level so the dial
    // shows the new device at once rather than at its first change.
    void Rebind()
    {
        lock (gate)
        {
            if (endpoint is not null)
            {
                endpoint.UnregisterControlChangeNotify(callback);
                Marshal.ReleaseComObject(endpoint);
                endpoint = null;
            }
            if (enumerator is null) return;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0 || device is null) return;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsctxAll, 0, out var activated) == 0 && activated is IAudioEndpointVolume volume)
            {
                endpoint = volume;
                endpoint.RegisterControlChangeNotify(callback);
            }
            Marshal.ReleaseComObject(device);
        }
        Current.Tap(changes.OnNext);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (endpoint is not null)
            {
                endpoint.UnregisterControlChangeNotify(callback);
                Marshal.ReleaseComObject(endpoint);
                endpoint = null;
            }
            if (enumerator is not null) enumerator.UnregisterEndpointNotificationCallback(watcher);
        }
        changes.OnCompleted();
    }

    const int DataFlowRender = 0;     // eRender: playback, not recording
    const int RoleMultimedia = 1;     // eMultimedia: what the volume flyout and the keyboard keys control
    const int ClsctxAll = 0x17;

    // --- the COM side -----------------------------------------------------------------------------------
    //
    // Only the vtable slots up to the last one called are declared, in their exact order: COM dispatches by
    // position, so a slot left out in the middle would call the wrong method, but slots after the last one
    // used can be left off.
    //
    // Public, though nothing outside should use them: the two callback interfaces are implemented HERE and
    // called by Windows, and the runtime only exposes public interfaces to COM through a managed object.
    // The rest follow so the signatures that mention one another agree.


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(nint notifyData);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    // AUDIO_VOLUME_NOTIFICATION_DATA: the event context GUID (16 bytes), then BOOL bMuted at 16 and float
    // fMasterVolume at 20. Read by offset rather than declared as a struct because its tail is a
    // variable-length channel array. The GUID says who made the change (see `context`).
    [ComVisible(true)]
    sealed class VolumeCallback(Action<Guid, VolumeLevel> changed) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notifyData)
        {
            // Never let an exception out: this is called from native code on Windows' own thread.
            try
            {
                if (notifyData != 0)
                    changed(Marshal.PtrToStructure<Guid>(notifyData),
                        new VolumeLevel(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(notifyData, 20)), Marshal.ReadInt32(notifyData, 16) != 0));
            }
            catch
            {
            }
            return 0;
        }
    }

    // Only the default render device changing for multimedia matters; everything else is acknowledged.
    // Rebinding from inside the callback is what Microsoft's guidance warns against (it can deadlock the
    // audio service), so it is handed to the thread pool.
    [ComVisible(true)]
    sealed class DeviceWatcher(Action rebind) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(int dataFlow, int role, string? id)
        {
            if (dataFlow == DataFlowRender && role == RoleMultimedia)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { rebind(); }
                    catch { }
                });
            return 0;
        }
    }
}
