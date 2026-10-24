using System.Diagnostics;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Microsoft.Win32;
using Deskbar.Core.Domain;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Shell;

// THE TASKBAR'S PINNED APPS, for the bar's dock line. Petre: "can you also show pinned icons somewhere?",
// "taskbar pinned", then "a" from three mockups: a line of them above the strip, like the Mac's dock.
//
// Read, and unpinned through the shell, but never pinned. Windows gives other programs no supported API for
// either; what it does still offer is each item's own "Unpin from taskbar" context-menu verb (canonical name
// "taskbarunpin"), measured as present on both kinds of pin on Petre's machine: a shortcut (Obsidian.lnk) and
// an installed app (YouTube Music). Petre: "rightclick should have unpin option". Pinning has no such verb a
// third party may invoke, so pinning stays on the taskbar.
//
// WHERE THEY LIVE, measured on Petre's machine before this was written:
//
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband, value "Favorites": every pin IN
//   TASKBAR ORDER, as a run of entries, each
//
//       [flag: 1 byte][size: uint32][an absolute ITEMIDLIST: `size` bytes, ending in its 0x0000]
//
//   which is undocumented, so it is decoded defensively: a size that runs off the end stops the walk and
//   what was read so far is kept. His ten decoded to ten, in the order his taskbar shows them, and the
//   shell names each list itself (SHCreateItemFromIDList), so nothing here parses a shell item by hand:
//
//       Microsoft Edge   C:\...\User Pinned\TaskBar\Microsoft Edge.lnk
//       YouTube Music    music.youtube.com-5929F88E_vezhnr0wkvrcy!App       (an installed app's ID)
//       Teams            C:\...\User Pinned\ImplicitAppShortcuts\...\Microsoft Teams - Work.lnk
//
//   "FavoritesChanges" in the same key counts edits, which is how the bar notices a pin being added.
//
// The User Pinned\TaskBar folder alone would have been simpler and is not enough: it holds no order, and
// none of the installed apps (YouTube Music, Files, Outlook) are in it.
public sealed record TaskbarPin(
    string Name,           // what the taskbar's tooltip says: "Google Chrome", "YouTube Music"
    string ParsingName,    // a shortcut's path, or an installed app's AppUserModelID
    string? AppId,         // the AppUserModelID it groups windows under, when it declares one
    string? TargetPath,    // what a shortcut starts, for matching windows by exe and for "Open file location"
    byte[] IdList)         // the shell item itself, kept for its icon
{
    // An installed (packaged) app has no file behind it; it is started through shell:AppsFolder.
    public bool IsInstalledApp => !Path.IsPathRooted(ParsingName);
}

// An icon as premultiplied BGRA pixels, top row first. Plain bytes so this layer stays free of WPF.
public sealed record PinImage(int Width, int Height, byte[] Pixels);

// What the bar needs, and an interface so the tests hand it a fake: the real one would put the pins of
// whoever runs the suite on every test bar.
public interface ITaskbarPins
{
    IReadOnlyList<TaskbarPin> Read();

    // Changes whenever a pin is added, removed or moved; cheap enough to poll.
    int Version { get; }

    Maybe<PinImage> ImageOf(TaskbarPin pin, int size);

    // The AppUserModelID a window is grouped under on the taskbar, if it declares one.
    string? AppIdOf(WindowHandle window);

    // `arguments`, when given, start the pin's TARGET exe with them rather than the shortcut as it is: VS Code's
    // "--new-window" (FloatingBar.OpenPin). Ignored for an installed app, which has no exe to pass them to.
    Result Launch(TaskbarPin pin, string? arguments = null);

    // Explorer, with what the pin starts selected: the taskbar's "Open file location".
    Result Reveal(TaskbarPin pin);

    // The item's own "Unpin from taskbar", exactly as its right-click menu in Explorer would run it.
    Result Unpin(TaskbarPin pin);

    // An app's name and icon by its AppUserModelID, from Start's own list of apps (shell:AppsFolder). For the
    // now-playing line, which knows the player only by the ID its media session gives.
    string? NameOfApp(string appId);
    Maybe<PinImage> ImageOfApp(string appId, int size);
}

public sealed class ShellTaskbarPins : ITaskbarPins
{
    const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    public int Version
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
                return key?.GetValue("FavoritesChanges") is int changes ? changes : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public IReadOnlyList<TaskbarPin> Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
            return key?.GetValue("Favorites") is byte[] blob
                ? SplitFavorites(blob).Select(Describe).Where(p => p is not null).Select(p => p!).ToList()
                : [];
        }
        catch
        {
            // A pin list that cannot be read is no pins, not a broken bar.
            return [];
        }
    }

    // The walk itself, kept pure so it can be tested on bytes: each entry's ITEMIDLIST, in order.
    internal static IReadOnlyList<byte[]> SplitFavorites(byte[] blob)
    {
        var lists = new List<byte[]>();
        var at = 0;
        while (at + 5 <= blob.Length)
        {
            var size = BitConverter.ToUInt32(blob, at + 1);
            var start = at + 5;
            // Too small to be a list, or running off the end: the format is not what was measured, so stop
            // rather than guess, keeping whatever was read cleanly before it.
            if (size < 2 || start + size > blob.Length) break;
            lists.Add(blob[start..(start + (int)size)]);
            at = start + (int)size;
        }
        return lists;
    }

    static TaskbarPin? Describe(byte[] idList) =>
        WithItem(idList, item =>
        {
            var name = DisplayName(item, SigdnNormalDisplay);
            var parsing = DisplayName(item, SigdnDesktopAbsoluteParsing);
            if (name is null || parsing is null) return null;
            return new TaskbarPin(name, parsing, StringProperty(item, AppUserModelId), StringProperty(item, LinkTargetParsingPath), idList);
        });

    public Maybe<PinImage> ImageOf(TaskbarPin pin, int size) =>
        WithItem(pin.IdList, item => item is IShellItemImageFactory factory ? Image(factory, size) : Maybe<PinImage>.None);

    public string? NameOfApp(string appId) =>
        WithParsed($@"shell:AppsFolder\{appId}", item => DisplayName(item, SigdnNormalDisplay), null);

    public Maybe<PinImage> ImageOfApp(string appId, int size) =>
        WithParsed($@"shell:AppsFolder\{appId}", item => item is IShellItemImageFactory factory ? Image(factory, size) : Maybe<PinImage>.None, Maybe<PinImage>.None);

    static T WithParsed<T>(string parsingName, Func<IShellItem2, T> use, T fallback)
    {
        try
        {
            var iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out var item) != 0 || item is null) return fallback;
            try { return use(item); }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch
        {
            return fallback;
        }
    }

    public string? AppIdOf(WindowHandle window)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window.Value, ref iid, out var store) != 0 || store is null) return null;
            try
            {
                var key = AppUserModelId;
                return store.GetValue(ref key, out var value) == 0 ? value.TakeString() : null;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    // A shortcut through the shell, the way a double-click on it would; an installed app through
    // shell:AppsFolder, which is how Start launches one. With arguments, the shortcut's target exe directly, in
    // its own folder, since a shortcut cannot be handed extra arguments through the shell.
    //
    // The two file arms go through DetachedLauncher, so under the Store copy what a pin starts breaks away from
    // Deskbar's package instead of running as part of it (store packaging design). `arguments` is ONE argument
    // (the only caller passes "--new-window"), sent as it is.
    //
    // The installed-app arm stays a plain shell start of Explorer: Explorer brokers the activation, so the app
    // is started by Explorer under its own package, not as Deskbar's child. The Store packaging spike checks
    // that this holds under the package.
    public Result Launch(TaskbarPin pin, string? arguments = null) =>
        (pin.IsInstalledApp, arguments, pin.TargetPath) switch
        {
            (true, _, _) => Result.Try(() =>
            {
                using var started = Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{pin.ParsingName}") { UseShellExecute = true });
            }, e => $"Could not start {pin.Name}: {e.Message}"),
            (false, { } args, { } target) => StartDetached(pin, target, [args], Path.GetDirectoryName(target)),
            _ => StartDetached(pin, pin.ParsingName, [], null),
        };

    static Result StartDetached(TaskbarPin pin, string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        DetachedLauncher.Start(program, arguments, workingDirectory).MapError(e => $"Could not start {pin.Name}: {e}");

    // Explorer's own `/select,"<path>"`, as ONE verbatim argument string with the path always quoted, which is
    // what this did before the package. As two C-runtime arguments the path went unquoted whenever it had no
    // space, and Explorer cut a path holding a comma at the comma. Through DetachedLauncher for the same
    // reason as Launch (StartVerbatim: neither the shell nor the packaged path quotes it a second time).
    public Result Reveal(TaskbarPin pin) =>
        (pin.TargetPath ?? (pin.IsInstalledApp ? null : pin.ParsingName)) is { } path
            ? DetachedLauncher.StartVerbatim("explorer.exe", $"/select,\"{path}\"", null)
                .MapError(e => $"Could not open the location of {pin.Name}: {e}")
            : Result.Failure($"{pin.Name} is an installed app, with no file location to open");

    // Through the item's context menu, the way Explorer runs a verb: build the menu (some handlers only know
    // their verbs once asked to fill one), then invoke by canonical name, which does not change with the
    // display language the way "Unpin from tas&kbar" does.
    public Result Unpin(TaskbarPin pin) =>
        WithItem(pin.IdList, item =>
        {
            var bhid = BhidSfuiObject;
            var iid = typeof(IContextMenu).GUID;
            if (item.BindToHandler(0, ref bhid, ref iid, out var handler) != 0 || handler == 0)
                return Result.Failure($"{pin.Name} offers no context menu to unpin it from");
            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(handler);
            Marshal.Release(handler);
            var popup = CreatePopupMenu();
            var verb = Marshal.StringToHGlobalAnsi("taskbarunpin");
            try
            {
                menu.QueryContextMenu(popup, 0, 1, 0x7FFF, CmfNormal);
                var invoke = new CommandInfo { Size = Marshal.SizeOf<CommandInfo>(), Verb = verb, Show = 1 };
                var hr = menu.InvokeCommand(ref invoke);
                return hr == 0 ? Result.Success() : Result.Failure($"Could not unpin {pin.Name} (0x{hr:X8})");
            }
            finally
            {
                Marshal.FreeHGlobal(verb);
                DestroyMenu(popup);
                Marshal.ReleaseComObject(menu);
            }
        }, Result.Failure($"Could not unpin {pin.Name}: the shell no longer knows it"));

    // --- shell items ----------------------------------------------------------------------------------

    static T WithItem<T>(byte[] idList, Func<IShellItem2, T> use) => WithItem(idList, use, default!);

    static T WithItem<T>(byte[] idList, Func<IShellItem2, T> use, T fallback)
    {
        // The list is copied out with two zero bytes on the end whatever it already ends with: a list the
        // shell reads past would be a crash, and an extra terminator costs nothing.
        var native = Marshal.AllocCoTaskMem(idList.Length + 2);
        try
        {
            Marshal.Copy(idList, 0, native, idList.Length);
            Marshal.WriteInt16(native, idList.Length, 0);
            var iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromIDList(native, ref iid, out var item) != 0 || item is null) return fallback;
            try
            {
                return use(item);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return fallback;
        }
        finally
        {
            Marshal.FreeCoTaskMem(native);
        }
    }

    static string? DisplayName(IShellItem2 item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var name) != 0 || name == 0) return null;
        try { return Marshal.PtrToStringUni(name); }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    static string? StringProperty(IShellItem2 item, PropertyKey key)
    {
        if (item.GetString(ref key, out var value) != 0 || value == 0) return null;
        try
        {
            var text = Marshal.PtrToStringUni(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    // The shell's own rendering of the icon at `size` pixels, alpha intact, read back as top-down BGRA.
    static Maybe<PinImage> Image(IShellItemImageFactory factory, int size)
    {
        if (factory.GetImage(new NativeSize { Width = size, Height = size }, SiigbfIconOnly | SiigbfBiggerSizeOk, out var bitmap) != 0 || bitmap == 0)
            return Maybe<PinImage>.None;
        var screen = GetDC(0);
        try
        {
            if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0) return Maybe<PinImage>.None;
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = info.Width,
                Height = -info.Height, // negative: top-down, matching WPF's row order
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[info.Width * 4 * info.Height];
            return GetDIBits(screen, bitmap, 0, (uint)info.Height, pixels, ref header, 0) == 0
                ? Maybe<PinImage>.None
                : new PinImage(info.Width, info.Height, pixels);
        }
        finally
        {
            ReleaseDC(0, screen);
            DeleteObject(bitmap);
        }
    }

    const uint CmfNormal = 0;
    static readonly Guid BhidSfuiObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    // CMINVOKECOMMANDINFO, the plain (ANSI verb) form. Sequential, so the marshaller pads it to x64's layout.
    [StructLayout(LayoutKind.Sequential)]
    public struct CommandInfo
    {
        public int Size, Mask;
        public nint Window, Verb, Parameters, Directory;
        public int Show, HotKey;
        public nint Icon;
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
    }

    const uint SigdnNormalDisplay = 0;
    const uint SigdnDesktopAbsoluteParsing = 0x80028000;
    const int SiigbfBiggerSizeOk = 0x1, SiigbfIconOnly = 0x4;

    static readonly PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    static readonly PropertyKey LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    // A PROPVARIANT holding a string, which is the only kind read here (VT_LPWSTR, 31). Laid out at its full
    // 24 bytes on x64 so the callee has room for any type it writes.
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type;
        ushort reserved1, reserved2, reserved3;
        public nint Value;
        nint padding;

        public string? TakeString()
        {
            try { return Type == 31 && Value != 0 ? Marshal.PtrToStringUni(Value) : null; }
            finally { PropVariantClear(ref this); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] public struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    // Vtable order matters (COM dispatches by slot), so IShellItem's five methods come first, then
    // IShellItem2's up to GetString, the last one used. Public because the runtime only marshals public
    // interfaces; nothing outside this class should use them.
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem2
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid iid, out nint result);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(nint other, uint hint, out int order);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid iid, out nint store);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, nint createObject, ref Guid iid, out nint store);
        [PreserveSig] int GetPropertyStoreForKeys(nint keys, uint count, int flags, ref Guid iid, out nint store);
        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey key, ref Guid iid, out nint list);
        [PreserveSig] int Update(nint bindContext);
        [PreserveSig] int GetProperty(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int GetCLSID(ref PropertyKey key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PropertyKey key, out long time);
        [PreserveSig] int GetInt32(ref PropertyKey key, out int value);
        [PreserveSig] int GetString(ref PropertyKey key, out nint value);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [DllImport("shell32.dll")] static extern int SHCreateItemFromIDList(nint idList, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetObjectW(nint obj, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
}
