using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Deskbar.Windows.Input;

namespace Deskbar.Windows.Tests;

// The language switcher's reading of a CLASSIC CONSOLE window (ConsoleWindowClass: cmd, PowerShell or claude started
// outside Windows Terminal). From an in-app report on 2026-11-25, "When I try to change the keyboard language, it goes
// back to GE", sent with a claude console in front.
//
// GetWindowThreadProcessId names the program running IN a console (cmd here), not conhost, which draws the window and
// takes its keyboard. That program's thread never sees a language change: measured the same day, it reported no layout
// at all for cmd while conhost's thread followed every switch. So the bar read nothing, painted the first installed
// language (GE on the reporter's machine) and stepped every click from there.
//
// This only READS the language, so it switches nobody's keyboard. The console is started hidden through conhost.exe
// itself, so it is a real ConsoleWindowClass window even where Windows Terminal is the default terminal.
public class InputLanguagesConsoleTests
{
    [Fact]
    public void A_console_window_reports_the_language_conhost_is_typing_in()
    {
        var title = $"deskbar-console-probe-{Guid.NewGuid():N}";
        using var conhost = Process.Start(new ProcessStartInfo("conhost.exe", $"cmd.exe /k title {title}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })!;
        try
        {
            var console = WaitForConsole(title);
            Assert.NotEqual(0, console);

            var language = new InputLanguages().Of(console);

            Assert.True(language.HasValue, "no input language read for a console window");
            Assert.Contains(new InputLanguages().Installed(), l => l.Layout == language.Value.Layout);
        }
        finally
        {
            // cmd runs as conhost's child; killing the tree takes both.
            conhost.Kill(entireProcessTree: true);
        }
    }

    // The console window carrying the title cmd's `title` command gave it, or 0 after ten seconds.
    static nint WaitForConsole(string title)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = ConsoleTitled(title);
            if (found != 0) return found;
            Thread.Sleep(100);
        }
        return 0;
    }

    static nint ConsoleTitled(string title)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            var text = new StringBuilder(256);
            var className = new StringBuilder(64);
            GetClassNameW(window, className, className.Capacity);
            GetWindowTextW(window, text, text.Capacity);
            if (className.ToString() == "ConsoleWindowClass" && text.ToString().Contains(title)) found = window;
            return found == 0;
        }, 0);
        return found;
    }

    delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
}
