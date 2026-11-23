using System.IO;
using System.Text.Json;
using Deskbar.Windows.Claude;

namespace Deskbar.Windows.Tests;

// The Windows Terminal fragment Deskbar installs for the "Deskbar Claude" profile. Every test uses a temp folder as
// %LOCALAPPDATA%, never the real one, and nothing starts a terminal.
public sealed class TerminalFragmentTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "deskbar-fragment-" + Guid.NewGuid().ToString("N"));

    string Fragments => Path.Combine(root, "Microsoft", "Windows Terminal", "Fragments");
    string FragmentFile => Path.Combine(Fragments, "Deskbar", "deskbar-claude.json");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    JsonElement Written()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FragmentFile));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_font_falls_back_to_Cascadia_Mono_when_JetBrains_Mono_is_missing()
    {
        Assert.True(TerminalFragment.Sync(true, root, _ => false).IsSuccess);
        Assert.Equal("Cascadia Mono", Written().GetProperty("profiles")[0].GetProperty("font").GetProperty("face").GetString());

        Assert.True(TerminalFragment.Sync(true, root, face => face == "JetBrains Mono").IsSuccess);
        Assert.Equal("JetBrains Mono", Written().GetProperty("profiles")[0].GetProperty("font").GetProperty("face").GetString());
    }

    [Fact]
    public void The_fragment_has_the_Dark_Modern_values()
    {
        Assert.True(TerminalFragment.Sync(true, root, _ => true).IsSuccess);
        var json = Written();

        var profile = json.GetProperty("profiles").EnumerateArray().Single();
        Assert.Equal(TerminalFragment.ProfileName, profile.GetProperty("name").GetString());
        Assert.Equal("Deskbar Claude", TerminalFragment.ProfileName);
        Assert.Equal("claude", profile.GetProperty("commandline").GetString());
        Assert.False(profile.GetProperty("hidden").GetBoolean());
        Assert.False(profile.TryGetProperty("startingDirectory", out _));
        Assert.Equal(10, profile.GetProperty("font").GetProperty("size").GetInt32());
        Assert.Equal(1.15, profile.GetProperty("lineHeight").GetDouble());
        Assert.Equal("14, 10, 14, 10", profile.GetProperty("padding").GetString());
        Assert.Equal("Deskbar Dark Modern", profile.GetProperty("colorScheme").GetString());
        Assert.Equal("bar", profile.GetProperty("cursorShape").GetString());
        Assert.Equal("#D97757", profile.GetProperty("cursorColor").GetString());
        Assert.Equal("cleartype", profile.GetProperty("antialiasingMode").GetString());
        // Claude's bold is \x1b[1m on RGB text, which Windows Terminal's default ("bright") draws exactly like plain text.
        Assert.Equal("bold", profile.GetProperty("intenseTextStyle").GetString());
        Assert.Equal("hidden", profile.GetProperty("scrollbarState").GetString());
        Assert.True(profile.GetProperty("suppressApplicationTitle").GetBoolean());

        var scheme = json.GetProperty("schemes").EnumerateArray().Single();
        Assert.Equal("Deskbar Dark Modern", scheme.GetProperty("name").GetString());
        Assert.Equal("#1F1F1F", scheme.GetProperty("background").GetString());
        Assert.Equal("#CCCCCC", scheme.GetProperty("foreground").GetString());
        Assert.Equal("#D97757", scheme.GetProperty("cursorColor").GetString());
        Assert.Equal("#264F78", scheme.GetProperty("selectionBackground").GetString());
        Assert.Equal("#F14C4C", scheme.GetProperty("red").GetString());
        Assert.Equal("#6E7681", scheme.GetProperty("brightBlack").GetString());
        Assert.Equal("#FFFFFF", scheme.GetProperty("brightWhite").GetString());
        // The name, sixteen ANSI colours plus background, foreground, cursor and selection.
        Assert.Equal(21, scheme.EnumerateObject().Count());
    }

    [Fact]
    public void Turning_on_removes_the_trial_fragment_and_keeps_other_fragments()
    {
        var trial = Path.Combine(Fragments, "DeskbarClaudeTry");
        var foreign = Path.Combine(Fragments, "SomeoneElse");
        Directory.CreateDirectory(trial);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(trial, "claude-looks.json"), "{}");
        File.WriteAllText(Path.Combine(foreign, "x.json"), "{}");

        Assert.True(TerminalFragment.Sync(true, root, _ => true).IsSuccess);

        Assert.False(Directory.Exists(trial));
        Assert.True(File.Exists(Path.Combine(foreign, "x.json")));
    }

    [Fact]
    public void Turning_off_removes_the_fragment_and_the_trial_one()
    {
        var trial = Path.Combine(Fragments, "DeskbarClaudeTry");
        var foreign = Path.Combine(Fragments, "SomeoneElse");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "x.json"), "{}");
        Assert.True(TerminalFragment.Sync(true, root, _ => true).IsSuccess);
        Directory.CreateDirectory(trial);

        Assert.True(TerminalFragment.Sync(false, root, _ => true).IsSuccess);

        Assert.False(Directory.Exists(Path.Combine(Fragments, "Deskbar")));
        // The trial folder was only Petre's picking aid, so it goes either way; a fragment that is not Deskbar's stays.
        Assert.False(Directory.Exists(trial));
        Assert.True(File.Exists(Path.Combine(foreign, "x.json")));
    }

    // Final review, item 11: the trial folder is clutter, so a file in it held open (Windows Terminal reading it, an
    // editor) must not stop Deskbar's profile being written, nor make the sync fail. Once it is let go, the next sync
    // removes it.
    [Fact]
    public void A_locked_trial_folder_does_not_block_the_profile()
    {
        var trial = Path.Combine(Fragments, "DeskbarClaudeTry");
        Directory.CreateDirectory(trial);
        var held = Path.Combine(trial, "claude-looks.json");
        File.WriteAllText(held, "{}");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(TerminalFragment.Sync(true, root, _ => true).IsSuccess);
            Assert.True(File.Exists(FragmentFile));
            Assert.True(Directory.Exists(trial));

            Assert.True(TerminalFragment.Sync(false, root, _ => true).IsSuccess);
            Assert.False(Directory.Exists(Path.Combine(Fragments, "Deskbar")));
        }

        Assert.True(TerminalFragment.Sync(true, root, _ => true).IsSuccess);
        Assert.False(Directory.Exists(trial));
    }

    [Fact]
    public void Turning_off_with_nothing_installed_succeeds() =>
        Assert.True(TerminalFragment.Sync(false, root, _ => true).IsSuccess);
}
