using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The permission questions on show and the slots their answers go through (PermissionDesk), and the rule the hook script
// prints by (PermissionAnswer). Hard properties held here: a wrong, reused or expired code answers nothing; every answer
// that is not a valid click prints nothing, never allow.
public sealed class PermissionDeskTests : IDisposable
{
    readonly string box = Path.Combine(Path.GetTempPath(), "deskbar-perm-" + Guid.NewGuid().ToString("N"));
    DateTimeOffset clock = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    PermissionDesk Desk() => new(box, () => clock);

    public void Dispose()
    {
        if (Directory.Exists(box)) Directory.Delete(box, true);
    }

    static PermissionRequest Request(string session = "s1", string command = "git push") =>
        new(session, "Bash", @"C:\repos\x", "default", [new PermissionField("command", command)]);

    // A slot as the script makes it: a fresh, empty folder (the code never goes through a file, the review's M1).
    string Slot(string name = "req-1-2")
    {
        Directory.CreateDirectory(Path.Combine(box, name));
        return name;
    }

    string AnswerText(string slot) => File.ReadAllText(Path.Combine(box, slot, PermissionDesk.AnswerFile));

    // What the hook would print for the slot's answer, as the script reads it.
    Maybe<string> Printed(string slot, string code) =>
        File.Exists(Path.Combine(box, slot, PermissionDesk.AnswerFile)) ? PermissionAnswer.LineFor(AnswerText(slot), code) : Maybe<string>.None;

    [Fact]
    public void A_registered_question_gets_a_fresh_32_hex_code_and_is_on_show()
    {
        var desk = Desk();
        var first = desk.Register(Slot("req-1-2"), Request()).Value;
        var second = desk.Register(Slot("req-3-4"), Request("s2")).Value;

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { "req-1-2", "req-3-4" }, desk.All.Select(p => p.Slot));
        Assert.True(desk.Asks("s1"));
        Assert.Equal(clock + PermissionDesk.HeldFor, desk.All[0].Until);
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("req-1-2\\..\\..")]
    [InlineData("C:\\temp")]
    [InlineData("req-1")]
    [InlineData("")]
    public void A_slot_name_the_script_would_not_make_is_refused(string name)
    {
        Assert.True(Desk().Register(name, Request()).IsFailure);
    }

    [Fact]
    public void A_slot_that_is_not_there_or_already_registered_is_refused()
    {
        var desk = Desk();
        Assert.True(desk.Register("req-9-9", Request()).IsFailure);
        Assert.True(desk.Register(Slot(), Request()).IsSuccess);
        Assert.True(desk.Register("req-1-2", Request()).IsFailure);
        Assert.Single(desk.All);
    }

    // A file planted in the slot before the question was asked is never trusted: the slot is refused, whatever the file
    // (an answer, or a reply.txt as the first script used: the code never goes through a file now, the review's M1).
    [Theory]
    [InlineData(PermissionDesk.AnswerFile)]
    [InlineData("reply.txt")]
    public void A_slot_holding_any_file_is_refused(string planted)
    {
        var slot = Slot();
        File.WriteAllText(Path.Combine(box, slot, planted), "0123 allow");

        Assert.True(Desk().Register(slot, Request()).IsFailure);
    }

    [Theory]
    [InlineData(PermissionDecision.Allow, PermissionAnswer.AllowLine)]
    [InlineData(PermissionDecision.Deny, PermissionAnswer.DenyLine)]
    [InlineData(PermissionDecision.AllowAuto, PermissionAnswer.AllowAutoLine)]
    public void A_click_writes_the_code_and_the_word_and_the_hook_prints_that_line(PermissionDecision decision, string line)
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Request()).Value;

        Assert.True(desk.Answer("req-1-2", decision).IsSuccess);

        Assert.Equal($"{code} {PermissionAnswer.WordOf(decision)}\n", AnswerText("req-1-2"));
        Assert.Equal(line, Printed("req-1-2", code).Value);
        Assert.Empty(desk.All);
        Assert.False(File.Exists(Path.Combine(box, "req-1-2", "answer.tmp")));
    }

    [Fact]
    public void Pass_hands_the_question_back_to_the_terminal()
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Request()).Value;

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Pass).IsSuccess);
        Assert.True(Printed("req-1-2", code).HasNoValue);
    }

    // A code answers once: the second click finds nothing and writes nothing.
    [Fact]
    public void A_reused_code_answers_nothing()
    {
        var desk = Desk();
        desk.Register(Slot(), Request());
        Assert.True(desk.Answer("req-1-2", PermissionDecision.Deny).IsSuccess);
        var first = AnswerText("req-1-2");

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Allow).IsFailure);
        Assert.Equal(first, AnswerText("req-1-2"));
    }

    // A slot name used again by a later hook gets a NEW code; the old one prints nothing there.
    [Fact]
    public void An_old_code_in_a_reused_slot_prints_nothing()
    {
        var desk = Desk();
        var old = desk.Register(Slot(), Request()).Value;
        desk.Forget("s1");
        var fresh = desk.Register(Slot(), Request()).Value;

        desk.Answer("req-1-2", PermissionDecision.Allow);

        Assert.True(Printed("req-1-2", old).HasNoValue);
        Assert.True(Printed("req-1-2", fresh).HasValue);
    }

    [Fact]
    public void An_expired_question_answers_nothing_and_its_slot_goes()
    {
        var desk = Desk();
        desk.Register(Slot(), Request());
        clock += PermissionDesk.HeldFor;

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Allow).IsFailure);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-2")));
    }

    [Fact]
    public void Expire_takes_off_only_the_questions_held_too_long()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request());
        clock += TimeSpan.FromMinutes(5);
        desk.Register(Slot("req-2-2"), Request("s2"));
        clock += PermissionDesk.HeldFor - TimeSpan.FromMinutes(5);

        Assert.True(desk.Expire());
        Assert.Equal("req-2-2", desk.All.Single().Slot);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));
        Assert.False(desk.Expire());
    }

    [Fact]
    public void The_script_waits_longer_than_the_card_is_offered() =>
        Assert.True(PermissionDesk.ScriptWait - PermissionDesk.HeldFor >= TimeSpan.FromSeconds(10));

    [Fact]
    public void Questions_of_several_sessions_are_kept_apart_and_forgotten_apart()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request("s1"));
        desk.Register(Slot("req-2-2"), Request("s2"));
        desk.Register(Slot("req-3-3"), Request("s3"));

        Assert.Single(desk.Of("s1"));
        Assert.True(desk.Forget("s1"));
        Assert.False(desk.Asks("s1"));
        Assert.Equal(new[] { "req-2-2", "req-3-3" }, desk.All.Select(p => p.Slot));
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));
        Assert.True(Directory.Exists(Path.Combine(box, "req-2-2")));
    }

    // ONE CARD PER SESSION (the review's L5): the user answered the old question in the terminal and the session asked
    // again at once. The newer question takes the older one off and deletes its slot, so the old card's click finds
    // nothing and writes nothing, and the old hook, its slot gone, ends printing nothing. Another session's stays.
    [Fact]
    public void A_newer_question_of_the_session_takes_the_older_one_off()
    {
        var desk = Desk();
        var old = desk.Register(Slot("req-1-1"), Request("s1", "first")).Value;
        desk.Register(Slot("req-2-2"), Request("s2"));
        var newer = desk.Register(Slot("req-3-3"), Request("s1", "second")).Value;

        Assert.Equal("req-3-3", desk.Of("s1").Single().Slot);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));
        Assert.True(Directory.Exists(Path.Combine(box, "req-2-2")));
        Assert.True(desk.Answer("req-1-1", PermissionDecision.Allow).IsFailure);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));

        Assert.True(desk.Answer("req-3-3", PermissionDecision.Allow).IsSuccess);
        Assert.True(Printed("req-3-3", old).HasNoValue);
        Assert.Equal(PermissionAnswer.AllowLine, Printed("req-3-3", newer).Value);
    }

    // The user answered in the terminal, or the session went: the session list no longer shows it waiting.
    [Fact]
    public void Keep_drops_the_sessions_that_no_longer_wait()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request("s1"));
        desk.Register(Slot("req-2-2"), Request("s2"));

        clock += TimeSpan.FromSeconds(30);
        // A read taken before the questions arrived says nothing about them.
        Assert.False(desk.Keep(session => session == "s2", seenBefore: clock - TimeSpan.FromMinutes(1)));
        Assert.True(desk.Keep(session => session == "s2", seenBefore: clock));
        Assert.Equal("s2", desk.All.Single().Request.SessionId);
    }

    [Fact]
    public void A_click_after_the_slot_went_writes_nothing()
    {
        var desk = Desk();
        desk.Register(Slot(), Request());
        Directory.Delete(Path.Combine(box, "req-1-2"), true);

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Allow).IsFailure);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-2")));
    }

    [Fact]
    public void ReleaseAll_and_Sweep_delete_slots_and_nothing_else()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request());
        Slot("req-5-5");
        Directory.CreateDirectory(Path.Combine(box, "someone-else"));

        Assert.True(desk.ReleaseAll());
        Assert.Empty(desk.All);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));

        Desk().Sweep();
        Assert.False(Directory.Exists(Path.Combine(box, "req-5-5")));
        Assert.True(Directory.Exists(Path.Combine(box, "someone-else")));
    }

    [Fact]
    public void Sweep_with_no_box_does_nothing() => new PermissionDesk(Path.Combine(box, "none"), () => clock).Sweep();

    // --- the rule the hook prints by: fail closed --------------------------------------------------------------------

    const string Code = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef allow\n", PermissionAnswer.AllowLine)]
    [InlineData("0123456789abcdef0123456789abcdef allow\r\n", PermissionAnswer.AllowLine)]
    [InlineData("0123456789abcdef0123456789abcdef deny", PermissionAnswer.DenyLine)]
    [InlineData("0123456789abcdef0123456789abcdef auto", PermissionAnswer.AllowAutoLine)]
    public void Only_the_right_code_and_a_known_word_give_a_line(string text, string line) =>
        Assert.Equal(line, PermissionAnswer.LineFor(text, Code).Value);

    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210 allow")]   // another hook's code
    [InlineData("0123456789ABCDEF0123456789ABCDEF allow")]   // not the code as made
    [InlineData("allow")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef pass")]
    [InlineData("0123456789abcdef0123456789abcdef edits")]   // the old edits word, gone with task bd55bd54
    [InlineData("0123456789abcdef0123456789abcdef fetch")]   // fetch has no line without a fetch
    [InlineData("0123456789abcdef0123456789abcdef Allow")]
    [InlineData("0123456789abcdef0123456789abcdef allow please")]
    [InlineData("0123456789abcdef0123456789abcdef  allow")]
    [InlineData(" 0123456789abcdef0123456789abcdef allow")]
    [InlineData("")]
    public void Anything_else_prints_nothing(string text) =>
        Assert.True(PermissionAnswer.LineFor(text, Code).HasNoValue);

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void A_hook_with_no_proper_code_prints_nothing_even_for_a_matching_file(string code) =>
        Assert.True(PermissionAnswer.LineFor($"{code} allow", code).HasNoValue);

    // The lines the hook prints are fixed text: the deny message is the fixed one, and the mode switch is for the
    // session only, never a settings file.
    [Fact]
    public void The_printed_lines_are_the_measured_ones()
    {
        Assert.Contains(PermissionAnswer.DenyMessage, PermissionAnswer.DenyLine);
        Assert.Contains("\"destination\":\"session\"", PermissionAnswer.AllowAutoLine);
        foreach (var line in new[] { PermissionAnswer.AllowLine, PermissionAnswer.DenyLine, PermissionAnswer.AllowAutoLine })
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal("PermissionRequest", parsed.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
        }
    }

    // --- the multiple-choice dialog (AskUserQuestion, task c047d0a0) --------------------------------------------------

    const string ColourInput =
        """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}],"multiSelect":false}]}""";

    static PermissionRequest Dialog(string session = "s1", string input = ColourInput) =>
        new(session, "AskUserQuestion", @"C:\repos\x", "default", [new PermissionField("questions", "...")], Input: input);

    static IReadOnlyDictionary<string, string> Blue => new Dictionary<string, string> { ["Pick a colour"] = "Blue" };

    // The hand-over: only answer.txt reaches the disk, naming the code and "dialog"; the line the hook prints stays in
    // Deskbar's memory until the script fetches it with its code (PermissionDesk.Fetch).
    [Fact]
    public void A_dialog_answer_writes_the_line_and_the_code_with_dialog()
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Dialog()).Value;
        var line = DialogAsk.Parse(ColourInput).Value.AnswerLine(Blue).Value;

        Assert.True(desk.AnswerDialog("req-1-2", Blue).IsSuccess);

        Assert.Equal($"{code} dialog\n", AnswerText("req-1-2"));
        // The line is on no disk: the slot holds the answer file and nothing else.
        Assert.Equal([PermissionDesk.AnswerFile], Directory.GetFiles(Path.Combine(box, "req-1-2")).Select(Path.GetFileName));
        Assert.DoesNotContain("allow", File.ReadAllText(Path.Combine(box, "req-1-2", PermissionDesk.AnswerFile)));
        var fetched = desk.Fetch("req-1-2", code).Value;
        Assert.Equal(line, fetched);
        Assert.Equal(line, PermissionAnswer.LineFor(AnswerText("req-1-2"), code, fetched).Value);
        Assert.Empty(desk.All);
        Assert.False(File.Exists(Path.Combine(box, "req-1-2", "answer.tmp")));
    }

    [Fact]
    public void LineFor_with_the_dialog_file_refuses_a_wrong_code_a_verdict_word_or_an_empty_file()
    {
        const string code = "0123456789abcdef0123456789abcdef";
        const string line = """{"hookSpecificOutput":{}}""";

        Assert.Equal(line, PermissionAnswer.LineFor($"{code} dialog\n", code, line + "\n").Value);
        Assert.True(PermissionAnswer.LineFor($"{code} allow\n", code, line).HasNoValue);
        Assert.True(PermissionAnswer.LineFor($"{code} deny\n", code, line).HasNoValue);
        Assert.True(PermissionAnswer.LineFor($"{code} edits\n", code, line).HasNoValue);
        Assert.True(PermissionAnswer.LineFor("fedcba9876543210fedcba9876543210 dialog\n", code, line).HasNoValue);
        Assert.True(PermissionAnswer.LineFor($"{code} dialog\n", code, "\r\n").HasNoValue);
        Assert.True(PermissionAnswer.LineFor($"{code} dialog\n", "not-a-code", line).HasNoValue);
    }

    // A slot that is not a dialog's never takes "dialog": the rule without a dialog file is the old one.
    [Fact]
    public void LineFor_without_a_dialog_file_refuses_dialog()
    {
        const string code = "0123456789abcdef0123456789abcdef";

        Assert.True(PermissionAnswer.LineFor($"{code} dialog\n", code).HasNoValue);
        Assert.Equal(PermissionAnswer.AllowLine, PermissionAnswer.LineFor($"{code} allow\n", code).Value);
    }

    // A dialog's slot takes its answers, never a click that allows or denies it; Pass still hands it to the terminal.
    [Theory]
    [InlineData(PermissionDecision.Allow)]
    [InlineData(PermissionDecision.Deny)]
    [InlineData(PermissionDecision.AllowAuto)]
    public void Allow_Deny_and_Auto_are_refused_for_a_dialog_and_write_nothing(PermissionDecision decision)
    {
        var desk = Desk();
        desk.Register(Slot(), Dialog());

        Assert.True(desk.Answer("req-1-2", decision).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
        Assert.Single(desk.All);
    }

    [Fact]
    public void Pass_hands_a_dialog_back_to_the_terminal()
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Dialog()).Value;

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Pass).IsSuccess);

        Assert.Equal($"{code} pass\n", AnswerText("req-1-2"));
        Assert.True(desk.Fetch("req-1-2", code).IsFailure);
        Assert.Empty(desk.All);
    }

    // An answer that does not cover every question is refused, nothing is written, and the question stays on show for
    // the user to finish.
    [Fact]
    public void A_dialog_answer_with_a_missing_answer_is_refused_and_the_question_stays()
    {
        var desk = Desk();
        desk.Register(Slot(), Dialog());

        Assert.True(desk.AnswerDialog("req-1-2", new Dictionary<string, string>()).IsFailure);
        Assert.True(desk.AnswerDialog("req-1-2", new Dictionary<string, string> { ["Pick a colour"] = " " }).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
        Assert.Single(desk.All);
        Assert.True(desk.AnswerDialog("req-1-2", Blue).IsSuccess);
    }

    [Fact]
    public void A_dialog_answer_for_a_permission_question_or_an_unknown_slot_is_refused()
    {
        var desk = Desk();
        desk.Register(Slot(), Request());

        Assert.True(desk.AnswerDialog("req-1-2", Blue).IsFailure);
        Assert.True(desk.AnswerDialog("req-9-9", Blue).IsFailure);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
    }

    // --- the plan (ExitPlanMode, task 8dbbaa0b): answered like a dialog, from its own card ---------------------------------

    const string PlanInput = """{"plan":"# Fix the bar\n\n1. Do it","planFilePath":"C:\\Users\\x\\.claude\\plans\\fix-the-bar.md"}""";

    static PermissionRequest Plan(string session = "s1", string input = PlanInput) =>
        new(session, "ExitPlanMode", @"C:\repos\x", "plan", [new PermissionField("plan", "# Fix the bar")], Input: input);

    [Theory]
    [InlineData(PlanDecision.Approve, "")]
    [InlineData(PlanDecision.ApproveEdits, "")]
    [InlineData(PlanDecision.KeepPlanning, "Split step 2")]
    public void A_plan_answer_writes_the_code_with_dialog_and_the_line_is_fetched_once(PlanDecision decision, string note)
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Plan()).Value;
        var line = PlanAsk.Parse(PlanInput).Value.AnswerLine(decision, note).Value;

        Assert.True(desk.AnswerPlan("req-1-2", decision, note).IsSuccess);

        Assert.Equal($"{code} dialog\n", AnswerText("req-1-2"));
        // The line is on no disk: the slot holds the answer file and nothing else.
        Assert.Equal([PermissionDesk.AnswerFile], Directory.GetFiles(Path.Combine(box, "req-1-2")).Select(Path.GetFileName));
        Assert.Empty(desk.All);
        Assert.True(desk.Fetch("req-1-2", "fedcba9876543210fedcba9876543210").IsFailure);
        Assert.Equal(line, desk.Fetch("req-1-2", code).Value);
        Assert.True(desk.Fetch("req-1-2", code).IsFailure);
        Assert.Equal(line, PermissionAnswer.LineFor(AnswerText("req-1-2"), code, line).Value);
    }

    // The note is checked before anything is written, so the plan stays on show for the user to type one.
    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Keep_planning_without_a_note_is_refused_and_the_plan_stays_on_show(string note)
    {
        var desk = Desk();
        desk.Register(Slot(), Plan());

        Assert.True(desk.AnswerPlan("req-1-2", PlanDecision.KeepPlanning, note).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
        Assert.Single(desk.All);
        Assert.True(desk.AnswerPlan("req-1-2", PlanDecision.KeepPlanning, "now a note").IsSuccess);
    }

    [Fact]
    public void A_plan_answer_for_a_slot_that_is_not_a_plan_or_is_unknown_is_refused()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request());
        desk.Register(Slot("req-2-2"), Dialog("s2"));

        Assert.True(desk.AnswerPlan("req-1-1", PlanDecision.Approve).IsFailure);
        Assert.True(desk.AnswerPlan("req-2-2", PlanDecision.Approve).IsFailure);
        Assert.True(desk.AnswerPlan("req-9-9", PlanDecision.Approve).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-1")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-2-2")));
        Assert.Equal(2, desk.All.Count);
    }

    // A plan whose input has no plan in it cannot be answered from a card, whatever slot it holds.
    [Fact]
    public void A_plan_answer_for_a_plan_request_without_a_plan_is_refused()
    {
        var desk = Desk();
        desk.Register(Slot(), Plan(input: "{}"));

        Assert.True(desk.AnswerPlan("req-1-2", PlanDecision.Approve).IsFailure);

        Assert.Single(desk.All);
    }

    [Theory]
    [InlineData(PermissionDecision.Allow)]
    [InlineData(PermissionDecision.Deny)]
    [InlineData(PermissionDecision.AllowAuto)]
    public void Allow_Deny_and_Auto_are_refused_for_a_plan_and_write_nothing(PermissionDecision decision)
    {
        var desk = Desk();
        desk.Register(Slot(), Plan());

        Assert.True(desk.Answer("req-1-2", decision).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
        Assert.Single(desk.All);
    }

    [Fact]
    public void Pass_hands_a_plan_back_to_the_terminal()
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Plan()).Value;

        Assert.True(desk.Answer("req-1-2", PermissionDecision.Pass).IsSuccess);

        Assert.Equal($"{code} pass\n", AnswerText("req-1-2"));
        Assert.Empty(desk.All);
    }

    // A plan has no questions to answer: AnswerDialog is the AskUserQuestion card's road only.
    [Fact]
    public void A_dialog_answer_for_a_plan_is_refused()
    {
        var desk = Desk();
        desk.Register(Slot(), Plan());

        Assert.True(desk.AnswerDialog("req-1-2", Blue).IsFailure);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(box, "req-1-2")));
        Assert.Single(desk.All);
    }

    // --- the fetch (PermissionDesk.Fetch): the line is handed over once, for the right code, within a minute -----------

    // A dialog answered from the desk: the code the registration returned, and the line waiting for it.
    (PermissionDesk Desk, string Code, string Line) Answered()
    {
        var desk = Desk();
        var code = desk.Register(Slot(), Dialog()).Value;
        Assert.True(desk.AnswerDialog("req-1-2", Blue).IsSuccess);
        return (desk, code, DialogAsk.Parse(ColourInput).Value.AnswerLine(Blue).Value);
    }

    [Fact]
    public void Fetch_returns_the_line_once_for_the_right_code_then_fails()
    {
        var (desk, code, line) = Answered();

        Assert.Equal(line, desk.Fetch("req-1-2", code).Value);
        Assert.True(desk.Fetch("req-1-2", code).IsFailure);
    }

    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("not-hex")]
    [InlineData("")]
    public void Fetch_fails_for_a_wrong_or_malformed_code_and_keeps_the_line_for_the_right_one(string wrong)
    {
        var (desk, code, line) = Answered();

        Assert.True(desk.Fetch("req-1-2", wrong).IsFailure);

        Assert.Equal(line, desk.Fetch("req-1-2", code).Value);
    }

    [Fact]
    public void Fetch_fails_for_an_unknown_slot_and_for_a_question_not_answered()
    {
        var (desk, code, _) = Answered();
        var other = desk.Register(Slot("req-3-4"), Dialog("s2")).Value;

        Assert.True(desk.Fetch("req-9-9", code).IsFailure);
        Assert.True(desk.Fetch("req-3-4", other).IsFailure);
        Assert.True(desk.Fetch("..\\x", code).IsFailure);
    }

    [Fact]
    public void Fetch_fails_past_the_fetch_window()
    {
        var (desk, code, _) = Answered();
        clock += PermissionDesk.FetchWindow;

        Assert.True(desk.Fetch("req-1-2", code).IsFailure);
    }

    [Fact]
    public void Fetch_still_answers_just_inside_the_fetch_window()
    {
        var (desk, code, line) = Answered();
        clock += PermissionDesk.FetchWindow - TimeSpan.FromSeconds(1);

        Assert.Equal(line, desk.Fetch("req-1-2", code).Value);
    }

    [Fact]
    public void Fetch_fails_after_ReleaseAll()
    {
        var (desk, code, _) = Answered();

        desk.ReleaseAll();

        Assert.True(desk.Fetch("req-1-2", code).IsFailure);
    }

    // A terminal or VS Code session is not in `claude agents`, so the list never judges its question (task 6306d261);
    // its transcript does, through Settle. A background question carries no mark, so Settle leaves it to the list.
    [Fact]
    public void An_unlisted_question_is_judged_by_its_transcript_not_by_the_session_list()
    {
        var desk = Desk();
        Directory.CreateDirectory(box);
        var transcript = Path.Combine(box, "t.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\"}\n");
        var mark = TranscriptMark.Take(transcript).Value;
        desk.Register(Slot("req-1-1"), Request("terminal"), listed: false, mark: mark);
        desk.Register(Slot("req-2-2"), Request("background"));
        clock += TimeSpan.FromMinutes(1);

        Assert.True(desk.Keep(_ => false, seenBefore: clock));
        Assert.Equal("terminal", desk.All.Single().Request.SessionId);

        Assert.False(desk.Settle());
        File.AppendAllText(transcript, "{\"type\":\"ai-title\"}\n");
        Assert.False(desk.Settle());
        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");
        Assert.True(desk.Settle());
        Assert.Empty(desk.All);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));
    }
}
