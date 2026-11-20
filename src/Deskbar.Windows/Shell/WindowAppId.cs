using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace Deskbar.Windows.Shell;

// Sets the AppUserModelID a window is grouped under on the taskbar. ShellTaskbarPins.AppIdOf READS it; this writes it.
//
// Why a window of ours needs its own: every window of one process is grouped under one taskbar button unless it says
// otherwise. A pinned workspace's taskbar button (Deskbar.App.TaskbarJumps) is one window per pinned workspace, and
// each must be a button of its own; measured in docs/superpowers/notes/2026-11-20-taskbar-jump-spike.md (finding 1):
// with a distinct ID per window, two windows of one process got two buttons.
//
// The interface is declared in full here, because the one in ShellTaskbarPins stops at GetValue and SetValue and
// Commit come after it in the vtable (COM dispatches by slot, so a shorter declaration cannot reach them).
public static class WindowAppId
{
    public static Result Set(nint window, string appId)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreForWindow(window, ref iid, out var store);
            if (hr != 0 || store is null) return Result.Failure($"No property store for the window (0x{hr:X8}).");
            var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(appId) }; // VT_LPWSTR
            try
            {
                var key = AppUserModelId;
                var set = store.SetValue(ref key, ref value);
                var commit = set == 0 ? store.Commit() : set;
                return commit == 0 ? Result.Success() : Result.Failure($"Setting the AppUserModelID failed (0x{commit:X8}).");
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Value);
                Marshal.ReleaseComObject(store);
            }
        }
        catch (COMException e)
        {
            return Result.Failure(e.Message);
        }
    }

    static readonly PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    // A PROPVARIANT holding a string, at its full 24 bytes on x64. The string is ours (CoTaskMem) and freed by Set.
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type;
        ushort reserved1, reserved2, reserved3;
        public nint Value;
        nint padding;
    }

    // Public because the runtime only marshals public interfaces; nothing outside this class should use it.
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
}
