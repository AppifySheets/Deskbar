using System.Text;

namespace Deskbar.Core.Control;

// The cmd.exe command line the Store copy uses to start a program on someone's behalf, so the program does
// not run as Deskbar's package (DetachedLauncher's packaged path).
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
//     /d /v:off /c start "" [/d "<folder>"] "<program>" <arguments...>
//
// `/d` skips cmd's AutoRun, so nothing a user hung on cmd runs first. `/v:off` keeps delayed expansion off even
// on a machine whose registry turns it on by default, where `!NAME!` in an argument would otherwise be expanded
// (measured: `/v:on` expands `!USERNAME!`, `/v:off` leaves it as written). `start` is there because it finds a bare
// program name through the PATH and App Paths the way Run does (`code`, `chrome`), which plain CreateProcess
// cannot. The empty "" is start's window title: without it the quoted program would be taken as the title.
//
// TWO PARSERS read this line, one after the other, and the quoting serves both:
//
//   1. cmd.exe reads the whole line first. It tracks a quote state that flips at every `"`; outside quotes
//      `& | < > ^` are operators and `^` escapes the next character, inside quotes they are plain text.
//      `%NAME%` is expanded everywhere, quoted or not, whenever NAME is a variable that exists.
//   2. `start` passes everything after the program to the program as it is, and the program splits it into
//      arguments with the C runtime's rules: quotes group, `""` inside quotes is a literal quote, and a run
//      of backslashes before a quote is halved.
//
// Every rule below was measured on Petre's machine (2026-10-24) by running such lines through cmd against a
// probe that printed the argv it received. The one that surprised: on a `cmd /c` command line, unlike in a
// batch file, `%%` is NOT collapsed to `%`, so doubling a percent hands the program `100%%`. What does stop
// expansion is a caret before the percent, and a caret only works OUTSIDE quotes, so a percent inside a
// quoted argument steps out of the quotes for its caret and back in: "q 100"^%"". The program then sees
// "q 100"%"" and the C runtime joins it back into `q 100%`.
//
// WHAT IS NOT GUARANTEED: an embedded quote. It is written as `""` inside the quotes, because that keeps cmd's
// quote state in step (a backslash-escaped `\"` would flip it and expose the operators after it). Programs
// that split their command line with the C runtime's rules read `""` as one literal quote and get the argument
// back whole. Programs that split it with CommandLineToArgvW (Chromium, Edge, Electron, VS Code's Code.exe) end
// the quoted region at the `""` instead, so a quote followed later by whitespace splits the argument there:
// `x"y z` arrives as [x"y] [z], and `say "hi there"` as [say "hi] [there"]. Measured by the Task 5 review with
// a pythonw argv echo reading the real command line, and confirmed by calling CommandLineToArgvW on the line.
// The .NET probe used for everything else could not see it, because its Environment.CommandLine is rebuilt
// from args rather than read from GetCommandLineW. Accepted because no caller passes an embedded quote (the
// pins pass `--new-window`, `restart-app` starts the exe bare), and serving both splitters would mean tracking
// cmd's quote parity through every argument.
public static class CommandLineQuoting
{
    // A percent inside quotes: close the quotes, caret-escaped percent, reopen them.
    const string PercentInsideQuotes = "\"^%\"";

    public static string ForCmdStart(string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        string.Join(" ", ["/d /v:off /c start \"\"", .. Folder(workingDirectory), ForStart(program), .. arguments.Select(ForProgram)]);

    // The same line, with the program's arguments given as ONE string already written the way the program must
    // receive it, for a program that does not split its command line with the C runtime's rules. Explorer is
    // the caller: `/select,"<path>"` is Explorer's own syntax, a switch and a quoted path joined by a comma,
    // and it is what the direct build always passed (ShellExecute's parameters, verbatim). Splitting it into
    // the two arguments `/select,` and `<path>` let the quoting above leave a path without spaces unquoted,
    // and Explorer then cuts a path holding a comma at the comma.
    //
    // Nothing here quotes for the program: the text keeps its own quotes, so `/select,"<path>"` is not quoted
    // a second time. Only cmd's reading is undone, with the same two measured rules as above, applied by
    // following cmd's quote state through the text: outside quotes a caret before `& | < > ^ %`, inside quotes
    // a percent steps out for its caret and back in. A program that joins adjacent quoted runs the C runtime's
    // way gets `"C:\p"%"x"` back as C:\p%x; whether Explorer does is not measured, and no revealed path with a
    // percent in it has been seen.
    public static string ForCmdStartVerbatim(string program, string arguments, string? workingDirectory) =>
        string.Join(" ", ["/d /v:off /c start \"\"", .. Folder(workingDirectory), ForStart(program), .. Verbatim(arguments)]);

    static IEnumerable<string> Verbatim(string arguments) =>
        arguments.Length == 0 ? [] : [ForCmdReading(arguments)];

    // Walks the text with cmd's quote state: every `"` flips it, as it does for cmd.
    static string ForCmdReading(string text) =>
        text.Aggregate(
            (Text: new StringBuilder(), Quoted: false),
            (state, c) => (c, state.Quoted) switch
            {
                ('"', var quoted) => (state.Text.Append(c), !quoted),
                ('%', true) => (state.Text.Append(PercentInsideQuotes), true),
                ('&' or '|' or '<' or '>' or '^' or '%', false) => (state.Text.Append('^').Append(c), false),
                _ => (state.Text.Append(c), state.Quoted),
            }).Text.ToString();

    // No folder, or an empty one, means none: cmd's own, as Process.Start treats an empty WorkingDirectory.
    static IEnumerable<string> Folder(string? workingDirectory) =>
        string.IsNullOrEmpty(workingDirectory) ? [] : ["/d", ForStart(workingDirectory)];

    // The program and the folder are read by `start` alone, which knows no backslash escapes and strips the
    // quotes itself, and a path cannot hold a quote, so only the percent rule applies. Always quoted, so a
    // path with spaces or shell operators in it is one token.
    static string ForStart(string path) => $"\"{path.Replace("%", PercentInsideQuotes)}\"";

    // An argument for the program. Quoted only when it has to be (a space, a tab, a quote, or nothing at all),
    // so `--new-window` arrives exactly as written, the same as Process.Start's ArgumentList would send it.
    static string ForProgram(string argument) =>
        argument.Length == 0 || argument.Any(c => c is ' ' or '\t' or '"') ? Quoted(argument) : Unquoted(argument);

    // Outside quotes every character cmd would act on gets a caret, the percent included.
    static string Unquoted(string argument) =>
        string.Concat(argument.Select(c => c is '&' or '|' or '<' or '>' or '^' or '%' ? $"^{c}" : c.ToString()));

    // Inside quotes cmd leaves the operators alone, so only quotes and percents need anything. Backslashes are
    // held back as a count until the next character shows whether a quote follows them: any quote this adds
    // (a doubled one, the step out for a percent, the closing one) would turn them into escapes, so a run
    // that ends at one is doubled, and any other run is written as it was.
    static string Quoted(string argument)
    {
        var (text, backslashes) = argument.Aggregate(
            (Text: new StringBuilder("\""), Backslashes: 0),
            (state, c) => c switch
            {
                '\\' => (state.Text, state.Backslashes + 1),
                '"' => (state.Text.Append('\\', state.Backslashes * 2).Append("\"\""), 0),
                '%' => (state.Text.Append('\\', state.Backslashes * 2).Append(PercentInsideQuotes), 0),
                _ => (state.Text.Append('\\', state.Backslashes).Append(c), 0),
            });
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
