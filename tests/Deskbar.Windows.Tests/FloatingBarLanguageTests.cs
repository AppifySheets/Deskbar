using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Windows.Input;

namespace Deskbar.Windows.Tests;

// The language switcher in the taskbar strip. Petre: "let's add a language switcher and have it show up if more
// than one language is present". Every bar here gets a FakeLanguages: the real InputLanguages would switch the
// keyboard of whoever runs the suite.
public class FloatingBarLanguageTests
{
    static readonly InputLanguage English = new(0x04090409, "EN", "English (United States)");
    static readonly InputLanguage Georgian = new(0x04370437, "KA", "Georgian (Georgia)");
    static readonly InputLanguage Russian = new(0x04190419, "RU", "Russian (Russia)");

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
        Assert.Equal("KA", Code(bar));
        bar.Close();
    });

    // The click goes to the next language in Windows' order, in the window you were typing in, and wraps round at
    // the end.
    [Fact]
    public void The_click_steps_to_the_next_language_and_wraps() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian, Russian);
        languages.ByWindow[Editor] = Georgian;
        var bar = BarWith(languages);

        bar.StepLanguage(+1);
        Assert.Equal("RU", Code(bar));
        bar.StepLanguage(+1);
        Assert.Equal("EN", Code(bar));
        bar.StepLanguage(-1);
        Assert.Equal("RU", Code(bar));

        Assert.Equal([(Editor, "RU"), (Editor, "EN"), (Editor, "RU")], languages.Switches);
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

    // Petre: "when changing a language with a shortcut it doesn't change what's showing on the bar". Measured with
    // a foreground hook: at the end of an Alt+Tab Windows often reports explorer's switcher window AFTER the window
    // you switched to, so the last foreground event named the switcher, and the bar read explorer's language every
    // tick while Ctrl+Shift changed the editor's. The tick now asks what is in front instead of trusting the order
    // the events arrived in.
    [Fact]
    public void A_shortcut_in_the_window_in_front_shows_even_after_events_arrive_out_of_order() => StaThread.Run(() =>
    {
        // Not the real switcher's handle (0x4210AC on his machine): a real one would be passed over by its class
        // on the event path, and this test is about the tick looking at what is in front.
        const nint taskSwitching = 0x1F000011;
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = English;
        languages.ByWindow[taskSwitching] = English;
        var bar = BarWith(languages);

        bar.NoteForeground(taskSwitching);
        languages.ByWindow[Editor] = Georgian; // left Ctrl+Shift, in the editor
        bar.PaintLanguage();

        Assert.Equal("KA", Code(bar));
        bar.Close();
    });

    // A remembered window that has since closed has no language; the one in front does.
    [Fact]
    public void A_closed_window_does_not_pin_the_button_to_the_first_language() => StaThread.Run(() =>
    {
        const nint closed = 0x1F000013;
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = Georgian;
        var bar = BarWith(languages);

        bar.NoteForeground(closed);
        bar.PaintLanguage();

        Assert.Equal("KA", Code(bar));
        bar.Close();
    });

    // With the bar itself in front (something else on the bar was clicked first, and that click does activate it),
    // the switch goes to the window you were in and hands it the foreground back so typing carries on there.
    [Fact]
    public void With_the_bar_itself_in_front_it_keeps_the_window_you_were_in_and_gives_it_the_foreground() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = Georgian;
        nint front = Editor;
        var handed = new List<nint>();
        var bar = BarWith(languages, () => front, handed.Add);

        front = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
        bar.PaintLanguage();
        Assert.Equal("KA", Code(bar));

        bar.StepLanguage(+1);
        Assert.Equal([(Editor, "EN")], languages.Switches);
        Assert.Equal([Editor], handed);
        bar.Close();
    });

    // Petre: "it's changing apps when i click on it and then it's inaccurate when i type", "not always, but very
    // inconsistent". The trace from his clicks: each one handed the foreground to a REMEMBERED window, a Teams chat
    // or an Edge window in turn, so a click brought one of them forward, and the switch landed in a window he was
    // not typing in. A click on the switcher no longer activates the bar (FloatingBar.Language.cs), so the window
    // you type in is still in front when the click arrives: that is the one switched, and the foreground is not
    // touched at all.
    [Fact]
    public void A_click_switches_the_window_in_front_and_moves_no_app_forward() => StaThread.Run(() =>
    {
        const nint teams = 0x1F000021, edge = 0x1F000022;
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[teams] = English;
        languages.ByWindow[edge] = English;
        nint front = teams;
        var handed = new List<nint>();
        var bar = BarWith(languages, () => front, handed.Add);

        bar.NoteForeground(teams);
        front = edge; // typing in Edge now, and its foreground event not seen yet

        bar.StepLanguage(+1);

        Assert.Equal([(edge, "KA")], languages.Switches);
        Assert.Empty(handed);
        Assert.Equal("KA", Code(bar));
        bar.Close();
    });

    // Petre: "it's not kat, it's ka". The two letter code, for every language. The layouts are the real ones
    // from his machine, Georgian's with the high word Windows gives it (0xF0AB) and sign extended, as it comes.
    [Fact]
    public void Languages_are_named_by_their_two_letter_code()
    {
        Assert.Equal("KA", InputLanguages.Describe(unchecked((nint)0xFFFFFFFFF0AB0437)).Code);
        Assert.Equal("EN", InputLanguages.Describe(0x04090409).Code);
        Assert.Equal("RU", InputLanguages.Describe(0x04190419).Code);
    }

    static Button Switcher(FloatingBar bar) =>
        bar.ShellStrip.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Input language");

    static string Code(FloatingBar bar) => ((TextBlock)Switcher(bar).Content).Text;

    // `front` stands in for the window Windows says is in the foreground; the editor unless a test says otherwise,
    // and never the real one, which is whatever the person running the suite has open. `handForeground` hears every
    // window the bar hands the foreground to, which in a test must never be a real one either.
    static FloatingBar BarWith(IInputLanguages languages, Func<nint>? front = null, Action<nint>? handForeground = null)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager)
        {
            Left = -32000, Top = -32000, SendShellKeys = _ => { },
            ForegroundWindow = front ?? (() => Editor),
            HandForegroundTo = handForeground ?? (_ => { }),
            InputLanguages = languages,
        };
        bar.ShowBar();
        bar.NoteForeground(Editor);
        bar.UpdateLayout();
        return bar;
    }
}
