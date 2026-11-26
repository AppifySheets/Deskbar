using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Deskbar.Windows.Shell;

namespace Deskbar.Windows.Tests;

// Reading a .lnk through the real shell. Each test writes a shortcut to a temp folder through IShellLinkW itself,
// then asks ShellBookmarkSources to resolve it.
public sealed class BookmarkShortcutTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-lnk-" + Guid.NewGuid().ToString("N"));

    public BookmarkShortcutTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    // Writes a shortcut; `target` null leaves the path unset (a Store app's shortcut has only an AppUserModelID).
    string Write(string? target, string? arguments, string? workingDirectory, string? appId)
    {
        var link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellBookmarkSources.ShellLinkClass)!)!;
        try
        {
            var shell = (ShellBookmarkSources.IShellLinkW)link;
            if (target is not null) Assert.Equal(0, shell.SetPath(target));
            if (arguments is not null) Assert.Equal(0, shell.SetArguments(arguments));
            if (workingDirectory is not null) Assert.Equal(0, shell.SetWorkingDirectory(workingDirectory));
            if (appId is not null)
            {
                var store = (WindowAppId.IPropertyStore)link;
                var key = ShellBookmarkSources.AppUserModelId;
                var value = new WindowAppId.PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(appId) };
                Assert.Equal(0, store.SetValue(ref key, ref value));
                Assert.Equal(0, store.Commit());
                Marshal.FreeCoTaskMem(value.Value);
            }
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".lnk");
            ((IPersistFile)link).Save(path, true);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [Fact]
    public void A_shortcut_resolves_to_its_target_arguments_and_folder()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Write(exe, "--flag \"a b\"", folder, null);

        var resolved = new ShellBookmarkSources().ResolveShortcut(lnk);

        Assert.True(resolved.HasValue);
        Assert.Equal(exe, resolved.Value.Target, ignoreCase: true);
        Assert.Equal("--flag \"a b\"", resolved.Value.Arguments);
        Assert.Equal(folder, resolved.Value.WorkingDirectory, ignoreCase: true);
        Assert.Null(resolved.Value.AppId);
    }

    [Fact]
    public void A_shortcut_with_an_app_id_reports_it()
    {
        var lnk = Write(null, null, null, "Contoso.Fake_abc123!App");

        var resolved = new ShellBookmarkSources().ResolveShortcut(lnk);

        Assert.True(resolved.HasValue);
        Assert.Equal("Contoso.Fake_abc123!App", resolved.Value.AppId);
    }

    [Fact]
    public void A_file_that_is_not_a_shortcut_resolves_to_nothing()
    {
        var path = Path.Combine(folder, "not.lnk");
        File.WriteAllText(path, "hello");
        Assert.True(new ShellBookmarkSources().ResolveShortcut(path).HasNoValue);
    }

    [Fact]
    public void Text_is_read_up_to_64_KB()
    {
        var path = Path.Combine(folder, "big.url");
        File.WriteAllText(path, new string('a', 100_000));
        Assert.Equal(64 * 1024, new ShellBookmarkSources().ReadText(path)!.Length);
    }
}
