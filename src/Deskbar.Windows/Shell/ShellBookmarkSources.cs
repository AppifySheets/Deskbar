using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;

namespace Deskbar.Windows.Shell;

// The disk and shell lookups the bookmark parser and window rule ask for (IBookmarkSources), done with the real
// shell. Every method answers "nothing" rather than throwing: a drop that cannot be read falls back to a plain name.
public sealed class ShellBookmarkSources : IBookmarkSources
{
    // A .url file is a few hundred bytes. The cap keeps a dropped file that merely ends in .url from being read whole.
    const int ReadTextCap = 64 * 1024;

    // What a .lnk points at, through IShellLinkW (Load by IPersistFile) and its IPropertyStore for the
    // AppUserModelID. A shortcut to a Store app has an AppUserModelID and no path; one to a program has a path.
    // Not Resolve()d: Resolve may search the disk for a moved target and show UI, and what was written is what we want.
    public Maybe<ShortcutTarget> ResolveShortcut(string lnkPath)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass)!);
            if (link is not IShellLinkW shell || link is not IPersistFile file) return Maybe<ShortcutTarget>.None;
            file.Load(lnkPath, 0); // STGM_READ; throws a COMException when the file is no shortcut, caught below

            var target = Text(b => shell.GetPath(b, b.Capacity, 0, 0));
            var arguments = Text(b => shell.GetArguments(b, b.Capacity));
            var folder = Text(b => shell.GetWorkingDirectory(b, b.Capacity));
            var appId = AppIdOf((WindowAppId.IPropertyStore)link);

            // Nothing at all: not a shortcut we can use.
            return target is null && appId is null
                ? Maybe<ShortcutTarget>.None
                : new ShortcutTarget(target, arguments, folder, appId);
        }
        catch
        {
            return Maybe<ShortcutTarget>.None;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    public string? DescribeExe(string exePath)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch
        {
            return null;
        }
    }

    public string? NameOfApp(string appId) => new ShellTaskbarPins().NameOfApp(appId);

    // At most 64 KB. Read as bytes and decoded by a StreamReader so a byte-order mark is honoured.
    public string? ReadText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[ReadTextCap];
            var count = stream.ReadAtLeast(bytes, ReadTextCap, throwOnEndOfStream: false);
            using var reader = new StreamReader(new MemoryStream(bytes, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    // The shell link's getters fill a caller-supplied buffer: ask for it, and treat an empty answer as none.
    static string? Text(Func<StringBuilder, int> ask)
    {
        // 32768 is the Windows command-line limit, so a .lnk's arguments cannot be longer than this and are not cut.
        var buffer = new StringBuilder(32768);
        return ask(buffer) == 0 && buffer.ToString().Trim() is { Length: > 0 } text ? text : null;
    }

    static string? AppIdOf(WindowAppId.IPropertyStore store)
    {
        var key = AppUserModelId;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            var id = value.Type == 31 && value.Value != 0 ? Marshal.PtrToStringUni(value.Value) : null; // VT_LPWSTR
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // PKEY_AppUserModel_ID.
    public static readonly WindowAppId.PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    // IShellLinkW in full, in vtable order (COM dispatches by slot). Public because the runtime only marshals
    // public interfaces; tests use it to write a shortcut. CharSet.Unicode via explicit LPWStr buffers.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, nint findData, uint flags);
        [PreserveSig] int GetIDList(out nint idList);
        [PreserveSig] int SetIDList(nint idList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCmd);
        [PreserveSig] int SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int max, out int icon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        [PreserveSig] int Resolve(nint window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref WindowAppId.PropVariant value);
}
