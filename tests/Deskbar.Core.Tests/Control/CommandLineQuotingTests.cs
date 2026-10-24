using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// The command line the Store copy hands cmd.exe when it starts a program on someone's behalf
// (DetachedLauncher's packaged path): `cmd /d /c start "" [/d "<cwd>"] "<program>" <args...>`.
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Two parsers read it in turn. cmd.exe reads the whole line first (quote state, carets, %VAR%), then
// `start` hands the tail after the program to the program verbatim, which splits it into argv with the
// usual C runtime rules. Every expected string here was run through cmd on Petre's machine (2026-10-24)
// against a probe that printed its argv, and arrived as the original arguments.
public class CommandLineQuotingTests
{
    // Measured: on a `cmd /c` command line (unlike inside a batch file) `%%` is NOT collapsed to `%`, so
    // doubling a percent would hand the program `100%%`. A caret outside quotes is what stops expansion
    // there, so an unquoted `100%` becomes `100^%`.
    [Fact]
    public void Quotes_spaces_embedded_quotes_and_percent_for_cmd_start() =>
        Assert.Equal(@"/d /c start """" /d ""C:\my dir"" ""C:\Program Files\X\x.exe"" ""a b"" ""say """"hi"""""" 100^%",
            CommandLineQuoting.ForCmdStart(@"C:\Program Files\X\x.exe", ["a b", "say \"hi\"", "100%"], @"C:\my dir"));

    [Fact]
    public void No_working_directory_means_no_d_switch() =>
        Assert.StartsWith(@"/d /c start """" ""code""", CommandLineQuoting.ForCmdStart("code", [], null));

    // An empty folder means "no folder", as it does to Process.Start, never `/d ""`.
    [Fact]
    public void An_empty_working_directory_means_no_d_switch_too() =>
        Assert.Equal(@"/d /c start """" ""code""", CommandLineQuoting.ForCmdStart("code", [], ""));

    [Fact]
    public void No_arguments_ends_at_the_program() =>
        Assert.Equal(@"/d /c start """" ""code""", CommandLineQuoting.ForCmdStart("code", [], null));

    // A caret is only an escape OUTSIDE quotes, and inside them cmd cannot be stopped from expanding
    // %VAR%, so a percent in a quoted argument steps out of the quotes for its caret and back in. The
    // program then sees "q 100"%"" which the C runtime joins back into `q 100%`.
    [Fact]
    public void A_percent_inside_a_quoted_argument_steps_out_of_the_quotes() =>
        Assert.EndsWith(@" ""q 100""^%""""", CommandLineQuoting.ForCmdStart("x", ["q 100%"], null));

    [Fact]
    public void Shell_operators_in_an_unquoted_argument_are_caret_escaped() =>
        Assert.EndsWith(@" a^&b^|c^<d^>e^^f", CommandLineQuoting.ForCmdStart("x", ["a&b|c<d>e^f"], null));

    // Inside quotes cmd treats & | < > ^ as plain text, so they need nothing.
    [Fact]
    public void Shell_operators_inside_a_quoted_argument_are_left_alone() =>
        Assert.EndsWith(@" ""c & d""", CommandLineQuoting.ForCmdStart("x", ["c & d"], null));

    [Fact]
    public void An_empty_argument_is_an_empty_pair_of_quotes() =>
        Assert.EndsWith(@" ""x"" """"", CommandLineQuoting.ForCmdStart("x", [""], null));

    // The C runtime reads backslashes before a quote as escapes, so a run of them that ends at any quote
    // the quoting adds (the closing one, a doubled one, the step out for a percent) is doubled.
    [Fact]
    public void Backslashes_before_a_quote_are_doubled() =>
        Assert.EndsWith(@" ""e f\\"" ""g\\""""h"" ""i j\\""^%""""",
            CommandLineQuoting.ForCmdStart("x", [@"e f\", @"g\""h", @"i j\%"], null));

    // The program and the folder are read by `start` alone, which knows no backslash escapes, so only
    // the percent rule applies to them.
    [Fact]
    public void A_percent_in_the_folder_or_program_steps_out_of_the_quotes_too() =>
        Assert.Equal(@"/d /c start """" /d ""C:\w 100""^%""x""^%"""" ""C:\p""^%""\x.exe""",
            CommandLineQuoting.ForCmdStart(@"C:\p%\x.exe", [], @"C:\w 100%x%"));
}
