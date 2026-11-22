using System.IO;
using System.Runtime.InteropServices;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests.Control;

// LongPath: the long form of an 8.3 short path, which is how a Claude session started with a short path reports its
// folder (docs/superpowers/notes/2026-11-22-claude-hook-probes.md). Against the real file system: a scratch folder with
// a long name, its short name read back from Windows, expanded again. Opens no window.
public sealed class LongPathTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "deskbar-longpath-" + Guid.NewGuid().ToString("N"));

    public LongPathTests() => Directory.CreateDirectory(root);

    [Fact]
    public void A_short_path_comes_back_long()
    {
        var folder = Path.Combine(root, "A folder with a long name");
        Directory.CreateDirectory(folder);
        var shortForm = ShortOf(folder);
        // A volume with 8.3 names turned off has no short form to test; the temp folder on this machine has them
        // (the probes' own scratch path was C:\Users\PETRE~1.CHI\...).
        Assert.True(shortForm.Contains('~'), $"no 8.3 name for {folder}");

        // The temp folder itself can be in short form here (TEMP is C:\Users\PETRE~1.CHI\... on this machine), so the
        // answer is checked for having no short segment left and ending in the long name, not against `folder`.
        var expanded = LongPath.Of(shortForm);
        Assert.DoesNotContain("~", expanded);
        Assert.EndsWith(@"\A folder with a long name", expanded);
        Assert.True(Directory.Exists(expanded));
    }

    // A path that does not exist (a folder deleted since) or is already long comes back as it was given.
    [Theory]
    [InlineData(@"C:\No\Such\FOLDER~1\here")]
    [InlineData(@"C:\Windows")]
    public void A_path_it_cannot_expand_comes_back_unchanged(string path) =>
        Assert.Equal(path, LongPath.Of(path));

    static string ShortOf(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, char[] shortPath, int length);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
