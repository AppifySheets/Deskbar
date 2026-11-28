using System.Diagnostics;
using System.IO;
using System.Text;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The permission hook's script, claude-permission-hook.cmd (ClaudeSkill.PermissionScript; Allow / Deny from a task's
// page), run for real by cmd in a scratch folder beside a stub deskbar.cmd that answers as `claude-permission` does: the
// code, one line. The test plays Deskbar's click by writing the slot's answer file the way PermissionDesk.Answer does.
//
// Hard properties held here: the script prints a line only for exactly "<its code> allow|deny|auto", the rule of
// PermissionAnswer.LineFor (each case is checked against that function); every other path prints nothing, never allow,
// including a planted answer built to break cmd's parsing; a slot that goes ends it at once.
public sealed class ClaudePermissionScriptTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("deskbar-perm-script-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    const string Code = "0123456789abcdef0123456789abcdef";

    static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // A Deskbar folder with the stub shim and the script. `code` is what the stub prints (null: nothing, as when Deskbar
    // is not running or the question stays in the terminal); `seconds` is the script's own deadline.
    // `stub` replaces the stub shim's text (a stub that reads stdin, or one that plants a file).
    string Prepared(string? code, int seconds = 25, string folderName = "Deskbar", string? stub = null)
    {
        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "code.txt"), code is null ? "" : code + "\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "deskbar.cmd"), stub ?? "@type \"%~dp0code.txt\"\r\n");
        var script = Path.Combine(folder, ClaudeSkill.PermissionHookScriptName);
        File.WriteAllText(script, ClaudeSkill.PermissionScript(seconds));
        return script;
    }

    static string Box(string script) => Path.Combine(Path.GetDirectoryName(script)!, PermissionDesk.FolderName);

    static Process Started(string script)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        var process = Process.Start(start)!;
        process.StandardInput.Write("""{"session_id":"s1","hook_event_name":"PermissionRequest","tool_name":"Bash"}""");
        process.StandardInput.Close();
        return process;
    }

    // The slot the script made: where the answer goes. The script reads an answer only once it has its code, so an answer
    // written as soon as the slot exists is read at the right time.
    static string Slot(string script, Process process)
    {
        var until = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < until && !process.HasExited)
        {
            var ready = Directory.Exists(Box(script))
                ? Directory.GetDirectories(Box(script)).FirstOrDefault()
                : null;
            if (ready is not null) return ready;
            Thread.Sleep(50);
        }
        throw new Xunit.Sdk.XunitException("the script made no slot");
    }

    // Writes the answer the way PermissionDesk.Answer does: a temporary file moved into place.
    static void Answer(string slot, string text)
    {
        var temporary = Path.Combine(slot, "answer.tmp");
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, Path.Combine(slot, PermissionDesk.AnswerFile));
    }

    static (int ExitCode, string Stdout) Finished(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Limit))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "still waiting");
        }
        return (process.ExitCode, stdout.Result);
    }

    // One full run: the script registers, the answer file is written, and what it printed comes back.
    (int ExitCode, string Stdout, string Slot) Answered(string text)
    {
        var script = Prepared(Code);
        using var process = Started(script);
        var slot = Slot(script, process);
        Answer(slot, text);
        var (exitCode, stdout) = Finished(process);
        return (exitCode, stdout, slot);
    }

    [Theory]
    [InlineData("allow", PermissionAnswer.AllowLine)]
    [InlineData("deny", PermissionAnswer.DenyLine)]
    [InlineData("auto", PermissionAnswer.AllowAutoLine)]
    public void The_right_code_and_a_word_print_that_line_and_the_slot_goes(string word, string line)
    {
        var (exitCode, stdout, slot) = Answered($"{Code} {word}\n");

        Assert.Equal(0, exitCode);
        Assert.Equal(line, stdout.TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(slot));
    }

    // The same rule as PermissionAnswer.LineFor, case by case: every one of these prints exactly what LineFor says, and
    // that is nothing for all but the first.
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef allow\r\n")]
    [InlineData("fedcba9876543210fedcba9876543210 allow\n")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF allow\n")]
    [InlineData("allow\n")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    [InlineData("0123456789abcdef0123456789abcdef pass\n")]
    [InlineData("0123456789abcdef0123456789abcdef Allow\n")]
    [InlineData("0123456789abcdef0123456789abcdef allow please\n")]
    [InlineData("0123456789abcdef0123456789abcdef  allow\n")]
    [InlineData(" 0123456789abcdef0123456789abcdef allow\n")]
    [InlineData("")]
    // Two lines, even when one of them is right.
    [InlineData("0123456789abcdef0123456789abcdef allow\n0123456789abcdef0123456789abcdef allow\n")]
    public void The_script_prints_exactly_what_LineFor_says(string text)
    {
        var (exitCode, stdout, _) = Answered(text);

        Assert.Equal(0, exitCode);
        Assert.Equal(PermissionAnswer.LineFor(text, Code).GetValueOrDefault(""), stdout.TrimEnd('\r', '\n'));
    }

    // A planted answer built to break cmd's parsing: quotes to end the comparison early, & and | to run a second command,
    // % and ! to expand a variable (the code itself). Each is text to the script, and it prints nothing.
    [Theory]
    [InlineData("x\" == \"x\" goto allow & rem \"")]
    [InlineData("\" == \"\" (echo {\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}) & rem \"")]
    [InlineData("%code% allow")]
    [InlineData("!code! allow")]
    [InlineData("a & echo {\"decision\":{\"behavior\":\"allow\"}} | more")]
    [InlineData("^\" allow")]
    public void A_planted_answer_meant_to_break_cmd_prints_nothing(string text)
    {
        var (exitCode, stdout, _) = Answered(text + "\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // Deskbar not running, or the question stays in the terminal: no code, and the script ends at once with nothing.
    [Fact]
    public void No_code_ends_it_at_once_printing_nothing()
    {
        var script = Prepared(null);
        var clock = Stopwatch.StartNew();
        using var process = Started(script);
        var (exitCode, stdout) = Finished(process);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Empty(Directory.GetDirectories(Box(script)));
    }

    // Deskbar dropped the question (the session moved on, the setting went off, Deskbar exited): the slot is gone, and the
    // script ends within a tick, printing nothing.
    [Fact]
    public void A_deleted_slot_ends_it_printing_nothing()
    {
        var script = Prepared(Code);
        using var process = Started(script);
        var slot = Slot(script, process);
        var clock = Stopwatch.StartNew();
        Directory.Delete(slot, true);
        var (exitCode, stdout) = Finished(process);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
    }

    // Nobody answers: at its own deadline it removes its slot and prints nothing, which leaves the terminal's dialog.
    [Fact]
    public void Its_own_deadline_ends_it_printing_nothing()
    {
        var script = Prepared(Code, seconds: 2);
        using var process = Started(script);
        var (exitCode, stdout) = Finished(process);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout);
        Assert.Empty(Directory.GetDirectories(Box(script)));
    }

    // A profile folder with a space, a letter outside ASCII and an ampersand still works (the waiter's I2 and M1).
    [Fact]
    public void An_awkward_folder_name_still_works()
    {
        var script = Prepared(Code, folderName: "Dé skbar & co");
        using var process = Started(script);
        Answer(Slot(script, process), $"{Code} allow\n");

        Assert.Equal(PermissionAnswer.AllowLine, Finished(process).Stdout.TrimEnd('\r', '\n'));
    }

    // --- a dialog's slot (AskUserQuestion, task c047d0a0) ------------------------------------------------------------

    // The line Deskbar hands over for a dialog: pure ASCII JSON on one line, with quotes, braces and a Georgian answer
    // escaped, the kind of text the script must let through unchanged.
    static readonly string DialogLine = DialogAsk.Parse(
            """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red","description":"warm"}]}]}""")
        .Value.AnswerLine(new Dictionary<string, string> { ["Pick a colour"] = "ფერი \"x\" & y | z ^ % !" }).Value;

    // A stub deskbar.cmd that answers both calls of the script: the registration (the code, and "dialog" when the reply
    // says so) and `claude-permission --fetch` (which reads its stdin and prints fetch.txt only when stdin is exactly
    // `expected`, as PermissionDesk.Fetch answers only the right code).
    static string FetchingStub(string expected) =>
        "@echo %* | findstr /c:\"--fetch\" >nul && goto fetch\r\n" +
        "@type \"%~dp0code.txt\"\r\n" +
        "@exit /b 0\r\n" +
        ":fetch\r\n" +
        "@set /p got=\r\n" +
        $"@if \"%got%\"==\"{expected}\" type \"%~dp0fetch.txt\"\r\n";

    // One full run against that stub: the script registers, Deskbar writes the answer file the way
    // PermissionDesk.AnswerDialog does (no dialog file), and what the script printed comes back. `fetched` is what the
    // stub's fetch prints ("" for nothing), `expectedCode` the code that fetch accepts.
    (int ExitCode, string Stdout, string Slot) DialogAnswered(string answerText, string? stubReply = null, string fetched = "",
        string expectedCode = Code)
    {
        var script = Prepared(stubReply ?? $"{Code} dialog", stub: FetchingStub(expectedCode));
        Prepare(script, "fetch.txt", fetched);
        using var process = Started(script);
        var slot = Slot(script, process);
        Answer(slot, answerText);
        var (exitCode, stdout) = Finished(process);
        return (exitCode, stdout, slot);
    }

    [Fact]
    public void A_dialog_answer_prints_exactly_the_line_the_fetch_returns_and_the_slot_goes()
    {
        var (exitCode, stdout, slot) = DialogAnswered($"{Code} dialog\n", fetched: DialogLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal(DialogLine, stdout.TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(slot));
        // The same rule as the C# mirror.
        Assert.Equal(DialogLine, PermissionAnswer.LineFor($"{Code} dialog\n", Code, DialogLine).Value);
    }

    // A picked suggestion on a tool permission's card (task bd55bd54): the tool slot takes "fetch" and prints the fetched
    // line; it never takes the dialog word, and a dialog's slot never takes fetch.
    static readonly string SuggestionLine = PermissionAnswer.SuggestionLine(PermissionRequest.Parse(
        """{"session_id":"s","hook_event_name":"PermissionRequest","tool_name":"Bash","permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git init *"}],"behavior":"allow","destination":"localSettings"}]}""")
        .Value.Suggestions[0]);

    [Fact]
    public void A_picked_suggestion_prints_the_fetched_line_and_the_slot_goes()
    {
        var (exitCode, stdout, slot) = DialogAnswered($"{Code} fetch\n", stubReply: Code, fetched: SuggestionLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal(SuggestionLine, stdout.TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(slot));
        Assert.Equal(SuggestionLine, PermissionAnswer.ToolLineFor($"{Code} fetch\n", Code, SuggestionLine).Value);
    }

    [Theory]
    [InlineData(Code, "dialog")]
    [InlineData(Code + " dialog", "fetch")]
    public void Fetch_and_dialog_words_are_each_taken_by_their_own_slot_only(string stubReply, string word)
    {
        var (exitCode, stdout, _) = DialogAnswered($"{Code} {word}\n", stubReply: stubReply, fetched: SuggestionLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.TrimEnd('\r', '\n'));
    }

    // The fetch's stdin is the script's own code: a server that accepts another code answers nothing.
    [Fact]
    public void The_fetch_sends_the_scripts_own_code()
    {
        var (exitCode, stdout, _) = DialogAnswered($"{Code} dialog\n", fetched: DialogLine + "\r\n",
            expectedCode: "fedcba9876543210fedcba9876543210");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // The fetch has nothing (spent, expired, Deskbar gone): nothing is printed and the terminal keeps the dialog.
    [Fact]
    public void A_fetch_that_prints_nothing_prints_nothing()
    {
        var (exitCode, stdout, slot) = DialogAnswered($"{Code} dialog\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
        Assert.False(Directory.Exists(slot));
    }

    // A dialog's slot takes nothing but "<code> dialog": a verdict word (which would let it through without the user's
    // picks) prints nothing, and so does a wrong code, even though the fetch would answer.
    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    [InlineData("edits")]
    [InlineData("auto")]
    [InlineData("pass")]
    public void A_verdict_word_on_a_dialog_slot_prints_nothing(string word)
    {
        var (exitCode, stdout, _) = DialogAnswered($"{Code} {word}\n", fetched: DialogLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    [Fact]
    public void A_wrong_code_on_a_dialog_slot_prints_nothing()
    {
        var (exitCode, stdout, _) = DialogAnswered("fedcba9876543210fedcba9876543210 dialog\n", fetched: DialogLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // A plain question (the stub says only the code) never takes "dialog", even though the fetch would answer.
    [Fact]
    public void The_word_dialog_on_a_plain_slot_prints_nothing()
    {
        var (exitCode, stdout, _) = DialogAnswered($"{Code} dialog\n", stubReply: Code, fetched: DialogLine + "\r\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // A second word in the registration's reply that is not "dialog" is no code at all: the script ends at once,
    // printing nothing, whatever it is answered with.
    [Theory]
    [InlineData("other")]
    [InlineData("allow")]
    [InlineData("DIALOG")]
    public void An_unknown_kind_in_the_reply_gives_no_code(string kind)
    {
        // The planting stub puts the answer in the slot during the call, so the test cannot lose the race against a
        // script that ends at once (see PlantingStub). Its own stub does not fetch, and none must be asked for.
        var script = Prepared($"{Code} {kind}", stub: PlantingStub(
            "copy /y \"%~dp0answer.src\" \"%%d\\answer.txt\" >nul"));
        Prepare(script, "answer.src", $"{Code} dialog\n");
        using var process = Started(script);

        var (exitCode, stdout) = Finished(process);
        Assert.True(File.Exists(Planted(script)));
        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    [Fact]
    public void The_script_is_pure_ascii() => Assert.All(ClaudeSkill.PermissionScript(), c => Assert.True(c < 128));

    // Claude Code runs a hook on Windows through Git Bash, `-c <command>`, with the command as Deskbar writes it into
    // settings.json (ClaudeSkill.HookCommand: the quoted path, forward slashes). Allowed, it prints the allow line.
    [Fact]
    public void Run_through_Git_Bash_as_Claude_Code_runs_it()
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        Assert.True(File.Exists(bash), $"Git Bash not found at {bash}");
        var script = Prepared(Code);
        var start = new ProcessStartInfo(bash)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ClaudeSkill.HookCommand(script));
        foreach (var name in new[] { "CLAUDE_PROJECT_DIR", "CLAUDE_JOB_DIR", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED" })
            start.Environment.Remove(name);
        using var hook = Process.Start(start)!;
        hook.StandardInput.Write("""{"session_id":"s1","hook_event_name":"PermissionRequest","tool_name":"Bash"}""");
        hook.StandardInput.Close();
        Answer(Slot(script, hook), $"{Code} allow\n");

        var (exitCode, stdout) = Finished(hook);
        Assert.Equal(0, exitCode);
        Assert.Equal(PermissionAnswer.AllowLine, stdout.TrimEnd('\r', '\n'));
    }

    // --- the code never touches the disk (the review's M1) -------------------------------------------------------------

    // While the script waits, no file in its permission box holds the code, at any moment: it went from the call's output
    // straight into a variable. Only the answer, written at the click, carries it (and the script then answers).
    [Fact]
    public void No_file_in_the_box_ever_holds_the_code_before_the_answer()
    {
        var script = Prepared(Code);
        using var process = Started(script);
        var slot = Slot(script, process);
        var seen = new List<string>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            foreach (var file in Directory.Exists(Box(script)) ? Directory.GetFiles(Box(script), "*", SearchOption.AllDirectories) : [])
            {
                try { if (File.ReadAllText(file).Contains(Code, StringComparison.OrdinalIgnoreCase)) seen.Add(file); }
                catch (IOException) { }
            }
            Thread.Sleep(10);
        }

        Assert.Empty(seen);
        Answer(slot, $"{Code} allow\n");
        Assert.Equal(PermissionAnswer.AllowLine, Finished(process).Stdout.TrimEnd('\r', '\n'));
    }

    // A STUB THAT PLANTS ITS FILES DURING THE CALL. A script whose reply is no code ends at once, and in a few hundred ms
    // removes its slot, so a test that waited to see the slot and only then wrote the answer lost the race whenever the
    // script was quicker than the test's 50 ms look (the full runs of 2026-11-26, "the script made no slot" after about
    // 140 ms, with the script already gone). Here the stub shim itself, while the script waits on the call, copies the
    // prepared files into the slot: they are there before the script could read anything, every time, and a marker file
    // proves the slot existed and was planted.
    static string PlantingStub(string files) =>
        $"@for /d %%d in (\"%~dp0{PermissionDesk.FolderName}\\req-*\") do @({files} & echo planted> \"%~dp0planted.txt\")\r\n" +
        "@type \"%~dp0code.txt\"\r\n";

    static string Planted(string script) => Path.Combine(Path.GetDirectoryName(script)!, "planted.txt");

    static void Prepare(string script, string name, string text) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(script)!, name), text, new UTF8Encoding(false));

    // A reply.txt planted in the slot (where the first script parked the code) is never read: Deskbar answered nothing,
    // so the script has no code and ends at once, printing nothing, whatever the planted reply and answer say.
    [Fact]
    public void A_planted_reply_file_is_ignored()
    {
        var script = Prepared(null, stub: PlantingStub(
            "copy /y \"%~dp0reply.src\" \"%%d\\reply.txt\" >nul & copy /y \"%~dp0answer.src\" \"%%d\\answer.txt\" >nul"));
        Prepare(script, "reply.src", Code + "\r\n");
        Prepare(script, "answer.src", $"{Code} allow\n");
        using var process = Started(script);

        var (exitCode, stdout) = Finished(process);
        Assert.True(File.Exists(Planted(script)));
        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // A reply meant to break cmd, answered with that very reply: the code must be 32 hex digits, and nothing from the reply
    // is parsed as a command, so each prints nothing.
    [Theory]
    [InlineData("x\" == \"x\" goto allow & rem \"")]
    [InlineData("\"")]
    [InlineData("%code%")]
    [InlineData("!code!")]
    [InlineData("^")]
    [InlineData("a&echo")]
    [InlineData("0123456789abcdef0123456789abcde")]     // 31 digits
    [InlineData("0123456789abcdef0123456789abcdef0")]   // 33 digits
    [InlineData("0123456789abcdef0123456789abcdeg")]    // not hex
    [InlineData("0123456789abcdef0123456789abcd!e")]
    public void A_reply_meant_to_break_cmd_prints_nothing(string reply)
    {
        var script = Prepared(reply, stub: PlantingStub("copy /y \"%~dp0answer.src\" \"%%d\\answer.txt\" >nul"));
        Prepare(script, "answer.src", $"{reply} allow\n");
        using var process = Started(script);

        var (exitCode, stdout) = Finished(process);
        Assert.True(File.Exists(Planted(script)));
        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout.Trim());
    }

    // The control for the two above: the same planting stub with the right code does print the allow line, so a planted
    // answer is read when it should be, and "prints nothing" above is the script refusing, not the answer missing.
    [Fact]
    public void The_planting_stub_with_the_right_code_answers()
    {
        var script = Prepared(Code, stub: PlantingStub("copy /y \"%~dp0answer.src\" \"%%d\\answer.txt\" >nul"));
        Prepare(script, "answer.src", $"{Code} allow\n");
        using var process = Started(script);

        Assert.Equal(PermissionAnswer.AllowLine, Finished(process).Stdout.TrimEnd('\r', '\n'));
        Assert.True(File.Exists(Planted(script)));
    }
    // The hook's JSON on stdin still reaches the call when its output is read straight into a variable: this stub
    // answers its code only when stdin holds a PermissionRequest.
    [Fact]
    public void The_hooks_stdin_still_reaches_Deskbar()
    {
        var stub = "@findstr /c:\"PermissionRequest\" >nul && type \"%~dp0code.txt\"\r\n";
        var script = Prepared(Code, stub: stub);
        using var process = Started(script);
        Answer(Slot(script, process), $"{Code} allow\n");

        Assert.Equal(PermissionAnswer.AllowLine, Finished(process).Stdout.TrimEnd('\r', '\n'));
    }
}
