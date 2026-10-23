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
        // Handed back once the click has finished, not during it (see StepLanguage).
        Assert.Empty(handed);
        Settle(bar);
        Assert.Equal([Editor], handed);
        bar.Close();
    });

    // Petre: "when i click the language switcher, can focus go back to the window it had before the language
    // switcher was clicked?" The trace of two clicks on 2.1.29 read `front 0x0`: nothing was in front when the click
    // arrived, so the switch went to the right window but no window had the keyboard. With nothing you type in at
    // the front, the window you were in gets the foreground back.
    [Fact]
    public void With_nothing_in_front_the_window_you_were_in_gets_the_foreground_back() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = English;
        nint front = Editor;
        var handed = new List<nint>();
        var bar = BarWith(languages, () => front, handed.Add);

        front = 0;
        bar.StepLanguage(+1);
        Settle(bar);

        Assert.Equal([(Editor, "KA")], languages.Switches);
        Assert.Equal([Editor], handed);
        bar.Close();
    });

    // Petre, on 2.1.30: "focus does go back to the window but i can't type unless i activate the window manually",
    // "so it doesn't really go back". All six of his clicks found the bar activated anyway (front 0x0 or the bar),
    // so the WM_MOUSEACTIVATE answer had not kept the foreground. The bar now carries WS_EX_NOACTIVATE for exactly
    // as long as the pointer is on the switcher, which Windows honours for every click, and loses it the moment
    // the pointer leaves, so the rest of the bar still activates as it always did.
    [Fact]
    public void The_bar_cannot_be_activated_while_the_pointer_is_on_the_switcher() => StaThread.Run(() =>
    {
        var languages = new FakeLanguages(English, Georgian);
        languages.ByWindow[Editor] = English;
        var bar = BarWith(languages);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
        bool NoActivate() => ((long)Monitoring.NativeMethods.GetWindowLongPtr(hwnd, Monitoring.NativeMethods.GWL_EXSTYLE) & 0x08000000) != 0;

        Assert.False(NoActivate());
        Switcher(bar).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        Assert.True(NoActivate());
        Switcher(bar).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
        Assert.False(NoActivate());
        bar.Close();
    });

    // Runs what the dispatcher has queued behind the current input, as a finished click would.
    static void Settle(FloatingBar bar) => bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

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
