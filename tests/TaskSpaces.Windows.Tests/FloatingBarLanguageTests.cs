using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Windows.Input;

namespace TaskSpaces.Windows.Tests;

// The language switcher in the taskbar strip. Petre: "let's add a language switcher and have it show up if more
// than one language is present". Every bar here gets a FakeLanguages: the real InputLanguages would switch the
// keyboard of whoever runs the suite.
public class FloatingBarLanguageTests
{
    static readonly InputLanguage English = new(0x04090409, "ENG", "English (United States)");
    static readonly InputLanguage Georgian = new(0x04370437, "KAT", "Georgian (Georgia)");
    static readonly InputLanguage Russian = new(0x04190419, "RUS", "Russian (Russia)");

    // A window the bar is told came to the front, standing in for the app you were typing in.
    const nint Editor = 0x5150;

    sealed class FakeLanguages(params InputLanguage[] installed) : IInputLanguages
    {
        public List<InputLanguage> List { get; } = [.. installed];
        public Dictionary<nint, InputLanguage> ByWindow { get; } = [];
        public List<(nint Window, string Code)> Switches { get; } = [];

        public IReadOnlyList<InputLanguage> Installed() => List;

        public Maybe<InputLanguage> Of(nint window) =>
            ByWindow.TryGetValue(window, out var language) ? language : Maybe<InputLanguage>.None;

        public Result SwitchTo(nint window, InputLanguage language)
        {
            Switches.Add((window, language.Code));
            ByWindow[window] = language;
            return Result.Success();
        }
    }

    [Fact]
    public void With_one_language_there_is_no_switcher() => StaThread.Run(() =>
    {
        var bar = BarWith(new FakeLanguages(English));
        Assert.Equal(Visibility.Collapsed, Switcher(bar).Visibility);
        bar.Close();
    });

    [Fact]
    public void With_two_it_shows_the_language_of_the_window_you_were_in() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = Georgian;
        var bar = BarWith(languages);

        Assert.Equal(Visibility.Visible, Switcher(bar).Visibility);
        Assert.Equal("KAT", Code(bar));
        bar.Close();
    });

    // The click goes to the next language in Windows' order, in the window you were typing in (not the bar,
    // which the click brought to the front), and wraps round at the end.
    [Fact]
    public void The_click_steps_to_the_next_language_and_wraps() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian, Russian);
        languages.ByWindow[Editor] = Georgian;
        var bar = BarWith(languages);

        bar.StepLanguage(+1);
        Assert.Equal("RUS", Code(bar));
        bar.StepLanguage(+1);
        Assert.Equal("ENG", Code(bar));
        bar.StepLanguage(-1);
        Assert.Equal("RUS", Code(bar));

        Assert.Equal([(Editor, "RUS"), (Editor, "ENG"), (Editor, "RUS")], languages.Switches);
        bar.Close();
    });

    // Removing the second language in Settings takes the switcher away again at the next look.
    [Fact]
    public void It_goes_when_the_second_language_does() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = English;
        var bar = BarWith(languages);
        Assert.Equal(Visibility.Visible, Switcher(bar).Visibility);

        languages.List.Remove(Georgian);
        bar.PaintLanguage();
        Assert.Equal(Visibility.Collapsed, Switcher(bar).Visibility);
        bar.Close();
    });

    static Button Switcher(FloatingBar bar) =>
        bar.ShellStrip.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Input language");

    static string Code(FloatingBar bar) => ((TextBlock)Switcher(bar).Content).Text;

    static FloatingBar BarWith(IInputLanguages languages)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, InputLanguages = languages };
        bar.ShowBar();
        bar.NoteForeground(Editor);
        bar.UpdateLayout();
        return bar;
    }
}
