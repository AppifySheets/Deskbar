using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The session picker's values (SessionSettings, SessionChoices) and the start and switch command lines built from them
// (ClaudeStart). Every value that reaches a command line is one of the menu's or a model name ClaudeStart.Model takes.
public class SessionSettingsTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    static TaskItem Task() =>
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Picker", Maybe<Guid>.None, TaskStatus.InProgress, "",
            Maybe<string>.None, T0, T0, T0, [], []);

    static SessionSettings Settings(string? model = null, string? effort = null, string? mode = null) =>
        new(Maybe.From(model), Maybe.From(effort), Maybe.From(mode));

    [Fact]
    public void Flags_are_written_only_for_what_is_set_in_model_effort_mode_order()
    {
        Assert.Empty(SessionSettings.None.Flags());
        Assert.Equal(["--effort", "low"], Settings(effort: "low").Flags());
        Assert.Equal(["--model", "sonnet", "--effort", "high", "--permission-mode", "plan"], Settings("sonnet", "high", "plan").Flags());
    }

    [Theory]
    [InlineData("LOW", "low")]
    [InlineData(" xhigh ", "xhigh")]
    [InlineData("Max", "max")]
    public void An_effort_is_one_of_the_five_levels_in_any_case(string given, string expected) =>
        Assert.Equal(expected, SessionChoices.Effort(given).Value);

    [Theory]
    [InlineData("ultra")]
    [InlineData("--model")]
    [InlineData("")]
    public void Anything_else_is_not_an_effort(string given) => Assert.True(SessionChoices.Effort(given).IsFailure);

    [Theory]
    [InlineData("acceptedits", "acceptEdits")]
    [InlineData("PLAN", "plan")]
    [InlineData("auto", "auto")]
    [InlineData("default", "default")]
    [InlineData("manual", "default")]
    public void A_mode_is_one_of_the_four_offered_and_manual_is_default(string given, string expected) =>
        Assert.Equal(expected, SessionChoices.Mode(given).Value);

    // Neither is offered, and neither may come in through the pipe or a hand-edited file either.
    [Theory]
    [InlineData("bypassPermissions", "bypassPermissions")]
    [InlineData("dontask", "dontAsk")]
    public void Bypass_and_dont_ask_are_refused_with_a_reason_naming_them(string given, string named) =>
        Assert.Contains(named, SessionChoices.Mode(given).Error);

    [Fact]
    public void The_menus_list_four_aliases_five_efforts_and_four_modes()
    {
        Assert.Equal(["fable", "opus", "sonnet", "haiku"], SessionChoices.Models.Select(m => m.Value));
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], SessionChoices.Efforts.Select(e => e.Value));
        Assert.Equal(["default", "acceptEdits", "plan", "auto"], SessionChoices.Modes.Select(m => m.Value));
        Assert.All(SessionChoices.Modes, m => Assert.NotEmpty(m.Hint));
    }

    [Fact]
    public void Of_takes_blanks_as_not_set_and_fails_on_the_first_bad_value()
    {
        Assert.Equal(Settings("claude-sonnet-5-5", mode: "acceptEdits"), SessionChoices.Of(" claude-sonnet-5-5 ", "", "acceptEdits").Value);
        Assert.Equal(SessionSettings.None, SessionChoices.Of(null, " ", null).Value);
        Assert.True(SessionChoices.Of("-p", null, null).IsFailure);
        Assert.True(SessionChoices.Of("opus", "huge", null).IsFailure);
        Assert.True(SessionChoices.Of("opus", "low", "bypassPermissions").IsFailure);
    }

    // What tasks.json holds goes on a command line later, so a bad value read from it is dropped, never kept.
    [Fact]
    public void Read_keeps_valid_values_in_their_normal_spelling_and_drops_the_rest()
    {
        Assert.Equal(Settings("opus", "high", "default"), SessionSettings.Read("opus", "HIGH", "manual"));
        Assert.Equal(Settings(effort: "low"), SessionSettings.Read("--dangerously-skip-permissions", "low", "dontAsk"));
        Assert.Equal(SessionSettings.None, SessionSettings.Read(null, "", "  "));
    }

    // Nothing picked: the start is exactly what it was before the picker existed.
    [Fact]
    public void A_start_with_no_pick_is_the_start_of_before()
    {
        var task = Task();
        var launch = ClaudeStart.LaunchSettings(Maybe<string>.None, Maybe<SessionSettings>.None, "opus");

        Assert.Equal(Settings("opus"), launch);
        Assert.Equal(ClaudeStart.Arguments(task, "opus"), ClaudeStart.Arguments(task, launch));
        Assert.Equal(["--bg", "--model", "opus", "--settings", ClaudeStart.RemoteControlOff, ClaudeStart.FirstMessage(task)],
            ClaudeStart.Arguments(task, launch));
    }

    [Fact]
    public void A_start_takes_the_given_model_then_the_picked_one_then_the_setting_and_effort_and_mode_only_from_the_pick()
    {
        var picked = Maybe.From(Settings("haiku", "low", "plan"));

        Assert.Equal(Settings("sonnet", "low", "plan"), ClaudeStart.LaunchSettings("sonnet", picked, "opus"));
        Assert.Equal(Settings("haiku", "low", "plan"), ClaudeStart.LaunchSettings(Maybe<string>.None, picked, "opus"));
        Assert.Equal(Settings("opus", "low"), ClaudeStart.LaunchSettings(Maybe<string>.None, Settings(effort: "low"), "opus"));
    }

    [Fact]
    public void The_start_arguments_carry_effort_and_mode_before_the_settings_layer_and_always_a_model()
    {
        var task = Task();

        Assert.Equal(["--bg", "--model", "sonnet", "--effort", "low", "--permission-mode", "acceptEdits",
                "--settings", ClaudeStart.RemoteControlOff, ClaudeStart.FirstMessage(task)],
            ClaudeStart.Arguments(task, Settings("sonnet", "low", "acceptEdits")));
        Assert.Equal(["--bg", "--model", ClaudeStart.DefaultModel, "--effort", "max", "--settings", ClaudeStart.RemoteControlOff,
                ClaudeStart.FirstMessage(task)],
            ClaudeStart.Arguments(task, Settings(effort: "max")));
    }

    // A copy does not inherit the original's saved options, so it gets the settings layer a start gets.
    [Fact]
    public void A_switch_resumes_the_full_id_with_every_start_flag_and_the_message_last()
    {
        const string id = "8c42135c-d6f4-4e0b-9d1a-000000000001";

        Assert.Equal(["--bg", "--resume", id, "--model", "sonnet", "--effort", "low", "--settings", ClaudeStart.RemoteControlOff],
            ClaudeStart.SwitchArguments(id, Settings("sonnet", "low")));
        Assert.Equal(["--bg", "--resume", id, "--model", ClaudeStart.DefaultModel, "--permission-mode", "plan",
                "--settings", ClaudeStart.RemoteControlOff, "Please look again"],
            ClaudeStart.SwitchArguments(id, Settings(mode: "plan"), "Please look again"));
    }

    [Fact]
    public void Settings_are_described_in_the_menus_words()
    {
        Assert.Equal("Sonnet · Extra high · Edit by itself", SessionChoices.Describe(Settings("sonnet", "xhigh", "acceptEdits")));
        Assert.Equal("claude-opus-5-5 · Plan first", SessionChoices.Describe(Settings("claude-opus-5-5", mode: "plan")));
        Assert.Equal("Claude Code's own settings", SessionChoices.Describe(SessionSettings.None));
    }
}
