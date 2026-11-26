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

    // A slot as the script makes it: a fresh folder, with the empty reply file cmd creates for the redirect.
    string Slot(string name = "req-1-2")
    {
        Directory.CreateDirectory(Path.Combine(box, name));
        File.WriteAllText(Path.Combine(box, name, PermissionDesk.ReplyFile), "");
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
        var second = desk.Register(Slot("req-3-4"), Request()).Value;

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { "req-1-2", "req-3-4" }, desk.Of("s1").Select(p => p.Slot));
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

    // An answer planted in the slot before the question was asked is never trusted: the slot is refused.
    [Fact]
    public void A_slot_holding_anything_but_the_reply_file_is_refused()
    {
        var slot = Slot();
        File.WriteAllText(Path.Combine(box, slot, PermissionDesk.AnswerFile), "0123 allow");

        Assert.True(Desk().Register(slot, Request()).IsFailure);
    }

    [Theory]
    [InlineData(PermissionDecision.Allow, PermissionAnswer.AllowLine)]
    [InlineData(PermissionDecision.Deny, PermissionAnswer.DenyLine)]
    [InlineData(PermissionDecision.AllowEdits, PermissionAnswer.AllowEditsLine)]
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
        desk.Register(Slot("req-2-2"), Request());
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
        desk.Register(Slot("req-3-3"), Request("s1", "rm x"));

        Assert.Equal(2, desk.Of("s1").Count);
        Assert.True(desk.Forget("s1"));
        Assert.False(desk.Asks("s1"));
        Assert.Equal("req-2-2", desk.All.Single().Slot);
        Assert.False(Directory.Exists(Path.Combine(box, "req-1-1")));
        Assert.True(Directory.Exists(Path.Combine(box, "req-2-2")));
    }

    // The user answered in the terminal, or the session went: the session list no longer shows it waiting.
    [Fact]
    public void Keep_drops_the_sessions_that_no_longer_wait()
    {
        var desk = Desk();
        desk.Register(Slot("req-1-1"), Request("s1"));
        desk.Register(Slot("req-2-2"), Request("s2"));

        Assert.True(desk.Keep(session => session == "s2"));
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
    [InlineData("0123456789abcdef0123456789abcdef edits", PermissionAnswer.AllowEditsLine)]
    public void Only_the_right_code_and_a_known_word_give_a_line(string text, string line) =>
        Assert.Equal(line, PermissionAnswer.LineFor(text, Code).Value);

    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210 allow")]   // another hook's code
    [InlineData("0123456789ABCDEF0123456789ABCDEF allow")]   // not the code as made
    [InlineData("allow")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef pass")]
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
        Assert.Contains("\"destination\":\"session\"", PermissionAnswer.AllowEditsLine);
        foreach (var line in new[] { PermissionAnswer.AllowLine, PermissionAnswer.DenyLine, PermissionAnswer.AllowEditsLine })
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal("PermissionRequest", parsed.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
        }
    }
}
