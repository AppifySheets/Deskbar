using System.Globalization;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Input;

// THE KEYBOARD INPUT LANGUAGES, for the bar's language switcher. Petre: "let's add a language switcher and have
// it show up if more than one language is present".
//
// All documented user32, no COM:
//   installed   GetKeyboardLayoutList, the same list Windows' own indicator by the clock cycles through, in the
//               order Settings shows it
//   current     GetKeyboardLayout of the THREAD that owns a window. A layout belongs to a thread, not to the
//               machine, so "which language is on" only has an answer for a given window
//   switching   WM_INPUTLANGCHANGEREQUEST posted to that window, which is how a program asks a window to
//               change its input language. With Windows' default setting (one input method for every app, not
//               "a different input method for each app window") the change then applies everywhere, exactly
//               as pressing Win+Space in that window would
//
// Each layout is an HKL: its low word is the language, its high word the keyboard. Two layouts of one language
// (English with a US and a UK keyboard, say) share the low word and so share a code; the name tells them apart.
public sealed record InputLanguage(nint Layout, string Code, string Name);

public interface IInputLanguages
{
    // Every installed input language, in Windows' order.
    IReadOnlyList<InputLanguage> Installed();

    // The language active in this window right now, or None for a window that is gone.
    Maybe<InputLanguage> Of(nint window);

    // Switch this window to the language, then hand it the foreground back, so typing carries on in it.
    Result SwitchTo(nint window, InputLanguage language);
}

public sealed class InputLanguages : IInputLanguages
{
    public IReadOnlyList<InputLanguage> Installed()
    {
        // Asked twice, as the API is meant to be used: once for the count, once to fill.
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];
        var layouts = new nint[count];
        var filled = GetKeyboardLayoutList(count, layouts);
        return layouts.Take(Math.Max(filled, 0)).Select(Describe).ToList();
    }

    public Maybe<InputLanguage> Of(nint window)
    {
        if (window == 0) return Maybe<InputLanguage>.None;
        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0) return Maybe<InputLanguage>.None;
        var layout = GetKeyboardLayout(thread);
        return layout == 0 ? Maybe<InputLanguage>.None : Describe(layout);
    }

    public Result SwitchTo(nint window, InputLanguage language)
    {
        if (window == 0) return Result.Failure("There is no window to switch the input language in.");
        // Posted, not sent: the window applies it when it next reads its queue, so a busy or hung window cannot
        // stall the bar. A window running elevated refuses a post from this one (UIPI), and that is the failure
        // reported here.
        if (!PostMessage(window, WM_INPUTLANGCHANGEREQUEST, 0, language.Layout))
            return Result.Failure($"Could not switch the input language (error {Marshal.GetLastWin32Error()}).");
        // The click on the bar took the foreground from that window; give it back. Allowed, because the bar is
        // the foreground window at this moment and may hand the foreground on.
        SetForegroundWindow(window);
        return Result.Success();
    }

    // The code is the TWO letter ISO name, upper case: "KA", "EN", "RU". It was the three letter one first,
    // which is what Windows' indicator shows for English ("ENG"), and for Georgian that is "KAT", from the
    // standard but not what anyone writes. Petre: "it's not kat, it's ka". One rule for every language, so
    // English is "EN" rather than "ENG" beside a "KA". A language .NET cannot name gets its hex id rather than
    // nothing, so the button never goes blank.
    internal static InputLanguage Describe(nint layout)
    {
        var languageId = (int)(layout & 0xFFFF);
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageId);
            return new InputLanguage(layout, culture.TwoLetterISOLanguageName.ToUpperInvariant(), culture.DisplayName);
        }
        catch (CultureNotFoundException)
        {
            var hex = languageId.ToString("X4", CultureInfo.InvariantCulture);
            return new InputLanguage(layout, hex, $"Language {hex}");
        }
    }

    const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
}
