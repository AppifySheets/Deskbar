using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Audio;

// THE PLAYBACK DEVICES, and switching which one is the default, for the volume dial's left click. Petre: "let's
// show sound devices on the right click of the speaker", then "let's actually show it on the left click, instead
// of muting".
//
// Listing is Core Audio, documented: the active render endpoints and each one's friendly name. SWITCHING is not:
// Windows offers no public call to change the default device, and every tool that does it (EarTrumpet,
// SoundSwitch, nircmd) goes through the same undocumented COM interface, IPolicyConfig, on the CPolicyConfigClient
// class. Its layout has been stable since Windows 7 and is what this declares. The default is set for all three
// roles (console, multimedia, communications), which is what choosing a device in Windows' own sound panel does.
//
// The volume dial follows the new default by itself: SystemVolume rebinds on Windows' default-device notification.
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public interface IAudioDevices
{
    // The active playback devices, the default first-class among them by IsDefault, in Windows' order.
    IReadOnlyList<AudioDevice> Outputs();

    Result MakeDefault(AudioDevice device);
}

public sealed class AudioDevices : IAudioDevices
{
    public IReadOnlyList<AudioDevice> Outputs()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)CoreAudio.CreateDeviceEnumerator();
            try
            {
                var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var current) == 0 && current is not null
                    ? IdOf(current)
                    : null;
                if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var devices) != 0 || devices is null) return [];
                devices.GetCount(out var count);
                return Enumerable.Range(0, (int)count)
                    .Select(i => devices.Item((uint)i, out var device) == 0 && device is not null ? Describe(device, defaultId) : null)
                    .OfType<AudioDevice>()
                    .ToList();
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            // No audio service, or a device that went away mid-list: nothing to choose from.
            return [];
        }
    }

    public Result MakeDefault(AudioDevice device) =>
        Result.Try(() =>
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClient, throwOnError: true)!)!;
            try
            {
                new[] { RoleConsole, RoleMultimedia, RoleCommunications }.ToList()
                    .ForEach(role => Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(device.Id, role)));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }, e => $"Could not switch the sound to {device.Name}: {e.Message}");

    static AudioDevice? Describe(IMMDevice device, string? defaultId)
    {
        try
        {
            var id = IdOf(device);
            if (id is null) return null;
            var name = device.OpenPropertyStore(StgmRead, out var store) == 0 && store is not null ? FriendlyName(store) : null;
            return new AudioDevice(id, name ?? id, id == defaultId);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    static string? IdOf(IMMDevice device)
    {
        if (device.GetId(out var id) != 0 || id == 0) return null;
        try { return Marshal.PtrToStringUni(id); }
        finally { Marshal.FreeCoTaskMem(id); }
    }

    static string? FriendlyName(IPropertyStore store)
    {
        try
        {
            var key = DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == 31 && value.Value != 0 ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    const int DataFlowRender = 0, DeviceStateActive = 1, StgmRead = 0;
    const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;
    static readonly Guid PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // --- COM, every slot up to the last one used, in vtable order ---------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type;
        ushort reserved1, reserved2, reserved3;
        public nint Value;
        nint padding;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? store);
        [PreserveSig] int GetId(out nint id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // Undocumented (see the header). Only SetDefaultEndpoint is called; the slots before it are declared so it
    // lands in the right place.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long defaultPeriodValue, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, out nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
}
