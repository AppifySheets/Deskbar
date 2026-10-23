using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Input;

namespace Deskbar.App;

// THE LANGUAGE SWITCHER in the taskbar strip. Petre: "let's add a language switcher and have it show up if more
// than one language is present", then, on the speaker, the same rule from the other side: "if there's only one
// sound output device, mute it, don't show just one".
//
//   shows      the keyboard input language by its two letter code ("KA", "EN"; see InputLanguages.Describe),
//              just left of the clock, where Windows puts its own indicator
//   hidden     with one input language or none, because there is nothing to switch to
//   click      the next language, in Windows' order, wrapping round
//   wheel      steps through them either way
//
// WHICH WINDOW. A layout belongs to a thread, so "the current language" means the language of the window you
// were typing in. Every look first takes the window in front right now, whenever that is one you type in
// (FollowForeground says why the events alone were wrong); a remembered one (NoteForeground keeps it, and never
// sees the bar itself) only answers while the bar itself or a shell flyout is in front.
//
// A CLICK ON THE SWITCHER DOES NOT ACTIVATE THE BAR. Petre: "it's changing apps when i click on it and then it's
// inaccurate when i type", "not always, but very inconsistent". It used to: the click took the foreground from the
// window you were typing in, so the switcher switched the REMEMBERED window and then handed it the foreground. The
// trace of his clicks showed what that cost: five clicks switched a Teams chat and an Edge window in turn, each
// click brought one of them forward, and twice the window the button read jumped to the other one 5 ms after the
// hand-off. The window that got the switch was a guess, and so was the window brought forward. Now the bar answers
// WM_MOUSEACTIVATE over the switcher with "do not activate" (KeepForegroundOnLanguageClick), so the window you type
// in is still in front when the click arrives: it is the one switched, and the foreground is not touched. Only when
// the bar itself is already in front (another part of it was clicked first) does the old path run: switch the
// remembered window and hand it the foreground back.
//
// The installed list and the current language are read again on every clock tick, because Windows raises no
// event this process can hear when Win+Space or Alt+Shift changes the language in another program, and when a
// language is added or removed in Settings. Both reads are a few user32 calls.
public partial class FloatingBar
{
    // Where the languages come from. Set by the composition root (InputLanguages); left unset, as every test but
    // the language ones leaves it, the switcher stays hidden and nothing reaches the machine's real keyboard.
    //
    // Painted the moment it is set, not at the clock's next tick. The button is built in the constructor, before
    // an object initializer can set this, and waiting for the tick left the switcher missing for the first five
    // seconds after a start, measured in the trace, which is exactly when a restarted bar gets looked at.
    public IInputLanguages? InputLanguages
    {
        get => inputLanguages;
        init
        {
            inputLanguages = value;
            PaintLanguage();
        }
    }

    readonly IInputLanguages? inputLanguages;

    // What Windows says is in the foreground right now. A seam for the tests and nothing else, like SendShellKeys:
    // a test reading the real foreground would read whatever the person running the suite has open.
    internal Func<nint> ForegroundWindow { get; init; } = Windows.Monitoring.NativeMethods.GetForegroundWindow;

    // Hands a window the foreground, for the one case a switch needs it (see StepLanguage). A seam for the same
    // reason: a test must never move the real foreground.
    internal Action<nint> HandForegroundTo { get; init; } = hwnd => Windows.Monitoring.NativeMethods.SetForegroundWindow(hwnd);

    Button? languageButton;
    TextBlock? languageCode;

    // The last window to have the foreground that was not a shell flyout (see NoteForeground). The window the
    // switcher reads and switches.
    nint languageTarget;

    // What the button showed last, so a tick that changes nothing touches nothing.
    string lastLanguagePaint = "";

    Button LanguageButton()
    {
        languageCode = new TextBlock
        {
            // 12, a step under the clock's date (13), so the code reads as part of the strip and fits in it.
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ClockForeground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        languageButton = new Button
        {
            Content = languageCode,
            // As wide as the code needs, with a little breathing room either side.
            Width = double.NaN,
            Height = StripButtonHeight,
            Padding = new Thickness(4, 0, 4, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetName(languageButton, "Input language");
        languageButton.Click += (_, _) =>
        {
            MarkPressConsumed();
            StepLanguage(+1);
        };
        languageButton.MouseWheel += (_, e) =>
        {
            // Handled, so the wheel steps the language and scrolls nothing behind it. Down is "next", as it is
            // for a list.
            e.Handled = true;
            StepLanguage(e.Delta < 0 ? +1 : -1);
        };
        clockTimer.Tick += (_, _) => PaintLanguage();
        SourceInitialized += (_, _) => KeepForegroundOnLanguageClick();
        PaintLanguage();
        return languageButton;
    }

    // Asked by Windows before a click activates the bar. Over the switcher the answer is "click, but do not
    // activate", so the window you were typing in keeps the foreground (see the comment at the top). The click
    // itself still arrives and the Button still raises Click: only the activation is withheld. The pointer's
    // position decides rather than WPF's IsMouseOver, which follows input WPF has processed and can lag the press.
    void KeepForegroundOnLanguageClick() =>
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(
            (nint _, int message, nint _, nint _, ref bool handled) =>
            {
                if (message != WM_MOUSEACTIVATE || !PointerOnLanguageButton()) return 0;
                handled = true;
                return MA_NOACTIVATE;
            });

    bool PointerOnLanguageButton()
    {
        if (languageButton is not { IsVisible: true } button || !Windows.Monitoring.NativeMethods.GetCursorPos(out var cursor)) return false;
        var at = button.PointFromScreen(new Point(cursor.X, cursor.Y));
        return at.X >= 0 && at.Y >= 0 && at.X < button.ActualWidth && at.Y < button.ActualHeight;
    }

    const int WM_MOUSEACTIVATE = 0x0021;
    const nint MA_NOACTIVATE = 3;

    // Called from NoteForeground with every foreground change but the bar's own.
    void NoteLanguageTarget(nint hwnd, bool isFlyout)
    {
        if (!IsTypingWindow(hwnd, isFlyout)) return;
        // Traced when it moves: the first trace of the "changing apps" report could show the target jumping between
        // two windows but not what moved it.
        if (ClickTrace.On && hwnd != languageTarget) ClickTrace.Write($"language target 0x{hwnd:X} (foreground event)");
        languageTarget = hwnd;
        // With "a different input method for each app window" on, the window that just came forward may be in a
        // different language from the last one, so show its language straight away rather than at the next tick.
        PaintLanguage();
    }

    // THE EVENTS ALONE ARE NOT ENOUGH. Petre: "when changing a language with a shortcut it doesn't change what's
    // showing on the bar". A foreground hook, run beside the bar, showed why: at the end of an Alt+Tab Windows
    // often reports explorer's switcher window ("Task Switching") AFTER the window you switched to, so the last
    // event, and with it the target, was the switcher, and every tick read explorer's language while left
    // Ctrl+Shift changed the editor's. A remembered target that has since closed did the same thing differently:
    // it has no language, so the button fell back to the first one installed and stayed there.
    //
    // So every look starts from what Windows says is in front NOW, and the remembered target only stands in while
    // that is the bar itself (it was clicked) or a shell flyout, which is the one time the window you were typing
    // in is not the one in front.
    void FollowForeground()
    {
        var front = ForegroundWindow();
        if (IsTypingWindow(front, isFlyout: ShellKeys.Recognise(front) is not null || (front != 0 && front == trayOverflow)))
            languageTarget = front;
    }

    // A window whose input language means something: one you type in. Not nothing, not a shell flyout, not
    // explorer's Alt+Tab switcher, and not one of this app's own windows, which a click on the bar brings to the
    // front without making them the window you were working in.
    bool IsTypingWindow(nint hwnd, bool isFlyout) =>
        hwnd != 0 && !isFlyout && !ShellKeys.IsTaskSwitching(hwnd) && !IsOwnWindow(hwnd);

    static bool IsOwnWindow(nint hwnd) =>
        Windows.Monitoring.NativeMethods.GetWindowThreadProcessId(hwnd, out var process) != 0 && process == (uint)Environment.ProcessId;

    nint LanguageWindow() => languageTarget != 0 ? languageTarget : ForegroundWindow();

    // The language one step along from the current one, switched to, and shown at once rather than at the next
    // tick (the switch is posted, so reading it back straight away could still see the old one).
    //
    // In the window in front at the click, when that is one you type in, which is the usual case now the click
    // leaves the foreground where it was. Only when the bar itself is in front does the switch go to the
    // remembered window, which then gets the foreground back so typing carries on there.
    internal void StepLanguage(int step)
    {
        if (InputLanguages is null) return;
        var installed = InputLanguages.Installed();
        if (installed.Count < 2) return;
        FollowForeground();
        var front = ForegroundWindow();
        var barInFront = front != 0 && IsOwnWindow(front);
        var window = LanguageWindow();
        var current = InputLanguages.Of(window).Map(now => installed.ToList().FindIndex(l => l.Layout == now.Layout)).GetValueOrDefault(-1);
        // A current language missing from the list (a layout Windows added for one program) counts as the first.
        var next = installed[((Math.Max(current, 0) + step) % installed.Count + installed.Count) % installed.Count];
        var outcome = InputLanguages.SwitchTo(window, next);
        if (outcome.IsSuccess && barInFront) HandForegroundTo(window);
        // The window in front is named beside the one switched, so a trace of a wrong switch says which it was.
        if (ClickTrace.On)
            ClickTrace.Write($"language: {next.Code} in 0x{window:X}, front 0x{front:X}{(barInFront ? " (the bar; foreground handed back)" : "")} "
                + (outcome.IsSuccess ? "ok" : outcome.Error));
        if (outcome.IsFailure) return;
        Show(next, installed.Count);
    }

    internal void PaintLanguage()
    {
        if (languageButton is null) return;
        var installed = InputLanguages?.Installed() ?? [];
        // The whole rule of the feature: no choice, no switcher.
        if (installed.Count < 2)
        {
            Show(null, installed.Count);
            return;
        }
        FollowForeground();
        Show(InputLanguages!.Of(LanguageWindow()).GetValueOrDefault(installed[0]), installed.Count);
    }

    void Show(InputLanguage? language, int count)
    {
        if (languageButton is null || languageCode is null) return;
        var paint = language is null ? "" : $"{language.Code}|{language.Name}";
        if (paint == lastLanguagePaint) return;
        lastLanguagePaint = paint;
        // Only when it changes, like the bar's other dumps, so the log stays readable with tracing on all day.
        if (ClickTrace.On) ClickTrace.Write($"language: {(language is null ? "hidden" : $"{language.Code} ({language.Name}) in 0x{LanguageWindow():X}")}, {count} installed");
        languageButton.Visibility = language is null ? Visibility.Collapsed : Visibility.Visible;
        languageCode.Text = language?.Code ?? "";
        languageButton.ToolTip = language is null ? null : $"{language.Name}. Click for the next of {count} languages.";
        // Showing or hiding the button changes what shares the strip with the clock, so the date may now fit or
        // not (FitClock).
        FitClock();
    }
}
