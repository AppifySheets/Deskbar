using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE MODEL, EFFORT AND PERMISSION MODE OF A TASK'S CLAUDE SESSION (the picker under the Inbox page's comment box,
// 2026-11-26: a row like the VS Code chat's, so the choice is visible and cheap to change per task). Three values
// Claude Code takes as flags when a session starts: `--model`, `--effort` and `--permission-mode` (`claude --help`,
// 2.1.332).
//
// Each one is a Maybe, and NONE MEANS "NOT PASSED": Claude Code's own setting decides (the user's `model`,
// `effortLevel` and `defaultMode` in settings.json, else the built-in default). Deskbar never invents a value for one
// nobody picked; that keeps the user's own Claude Code settings in charge of everything he did not choose here.
//
// WHERE THEY ARE USED. TaskSession.LaunchedWith is what Deskbar passed when it started a session (or the copy a switch
// made, see SettingsSwitch); TaskItem.Wanted is the user's pick for the task. Both are stored in tasks.json, both are
// read back through Read, which drops anything that is not a valid choice: a value from a hand-edited file goes on a
// command line later, so it is held to the same rule as one typed in.
//
// WHY ONLY THE START NAMES THEM (measured 2026-11-26, Claude Code 2.1.332): a background session woken with a bare
// `claude --bg --resume <id>` keeps its saved options, effort and mode among them ("woke session ... with its saved
// options (--effort, --setting-sources, --model, --permission-mode)"), and a mode a permission hook switched with setMode
// survived a stop and wake too. Any flag on that resume starts a COPY under a new id instead ("keeps its own saved
// options, so the flags you passed started a copy as ..."). So these flags go on a start, or on the resume that makes a
// switch's copy (ClaudeStart.SwitchArguments), and never on an ordinary wake. (A `claude -p --resume` behaves otherwise:
// it keeps the id and drops effort and mode back to the defaults. Deskbar resumes nothing with -p.)
public sealed record SessionSettings(Maybe<string> Model, Maybe<string> Effort, Maybe<string> Mode)
{
    /// <summary>Nothing picked: every flag left to Claude Code's own settings.</summary>
    public static readonly SessionSettings None = new(Maybe<string>.None, Maybe<string>.None, Maybe<string>.None);

    /// <summary>Whether nothing is set.</summary>
    public bool IsEmpty => Model.HasNoValue && Effort.HasNoValue && Mode.HasNoValue;

    /// <summary>
    /// The flags these settings put on a `claude` command line, in the order the start writes them: `--model`,
    /// `--effort`, `--permission-mode`, each only when set.
    /// </summary>
    public IReadOnlyList<string> Flags() =>
    [
        .. Model.Match(m => new[] { "--model", m }, Array.Empty<string>),
        .. Effort.Match(e => new[] { "--effort", e }, Array.Empty<string>),
        .. Mode.Match(p => new[] { "--permission-mode", p }, Array.Empty<string>),
    ];

    /// <summary>
    /// Settings as stored, read tolerantly: each value that is a valid choice (<see cref="SessionChoices"/>) is kept in
    /// its normal spelling, anything else (missing, blank, unknown, a leading dash) reads as not set. Never fails, so one
    /// odd value in tasks.json costs that value and never the file.
    /// </summary>
    public static SessionSettings Read(string? model, string? effort, string? mode) =>
        new(Valid(model, SessionChoices.Model), Valid(effort, SessionChoices.Effort), Valid(mode, SessionChoices.Mode));

    /// <summary>
    /// What a hook says the session runs on (ClaudeHookEvent.Model, Effort, PermissionMode), as given. For showing only:
    /// a value seen here can be one the picker refuses (bypassPermissions), so it never reaches a command line without
    /// going through <see cref="Read"/> or SessionChoices.
    /// </summary>
    public static SessionSettings Observed(ClaudeHookEvent hook) => new(hook.Model, hook.Effort, hook.PermissionMode);

    static Maybe<string> Valid(string? given, Func<string, Result<string>> check) =>
        string.IsNullOrWhiteSpace(given) ? Maybe<string>.None
        : check(given) is { IsSuccess: true } ok ? ok.Value
        : Maybe<string>.None;
}

/// <summary>One entry of a picker menu: the value the flag takes, the word the menu shows, and its one-line explanation.</summary>
public sealed record SessionChoice(string Value, string Label, string Hint = "");

// WHAT THE PICKER OFFERS, and the rule every value must pass before it reaches a command line (these menus agreed on
// 2026-11-26).
//
//   * MODELS: the aliases. An alias follows the latest model by itself ("Use an alias for the latest model", `claude
//     --help`; docs, model-config), so a new model needs no Deskbar release. Anything else ("Other…") goes through
//     ClaudeStart.Model, which takes an alias or a full name and refuses what `claude` could read as another argument.
//     There is no CLI command that lists the models an account has (docs, model-config), so a model the plan or an
//     admin forbids shows up as Claude's own refusal, not as a missing menu entry.
//   * EFFORTS: the five levels of `--effort` (`claude --help`). "Model default" is no value at all (None): the model's
//     own default, medium for the 5.5 models (docs, model-config), or the user's effortLevel.
//   * MODES: four of the six `--permission-mode` choices. `default` is Claude Code's manual mode; `--help` lists it as
//     `manual` and the docs call that an alias of `default`, so `manual` is read as `default` and `default` is what is
//     written, the spelling a background start was measured to accept (2026-11-26).
//     - bypassPermissions is NOT offered and is refused here: the docs say "Only for isolated containers and VMs", a
//       background start refuses it unless the user accepted it in his own settings, and it cannot be switched to in a
//       running session. Deskbar must never be the thing that turned the checks off.
//     - dontAsk is NOT offered and is refused here: anything that would prompt is silently denied, and a task session
//       needs its `deskbar task …` commands. (The manager session uses it, with its own allow list, in
//       ManagerSession.StartArguments; that start does not come through here.)
public static class SessionChoices
{
    /// <summary>The model aliases the menu lists, strongest first (the mockup's order).</summary>
    public static readonly IReadOnlyList<SessionChoice> Models =
    [
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku"),
    ];

    /// <summary>The effort levels the menu lists, lowest first. "Model default" is not among them: it is no value.</summary>
    public static readonly IReadOnlyList<SessionChoice> Efforts =
    [
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "Extra high"),
        new("max", "Max"),
    ];

    /// <summary>The permission modes the menu lists, with what each lets Claude do without asking.</summary>
    public static readonly IReadOnlyList<SessionChoice> Modes =
    [
        new("default", "Ask before edits", "asks before each change and each command"),
        new("acceptEdits", "Edit by itself", "changes files itself, still asks for commands"),
        new("plan", "Plan first", "reads and plans, changes nothing"),
        new("auto", "Auto", "decides itself what is safe to do"),
    ];

    /// <summary>A model as `--model` takes it (ClaudeStart.Model: an alias or a full name), or why it is not one.</summary>
    public static Result<string> Model(string given) => ClaudeStart.Model(given);

    /// <summary>An effort level from <see cref="Efforts"/>, in any case, in its listed spelling; or why it is not one.</summary>
    public static Result<string> Effort(string given) =>
        Efforts.TryFirst(e => e.Value.Equals(given.Trim(), StringComparison.OrdinalIgnoreCase))
            .Map(e => e.Value)
            .ToResult($"'{given}' is not an effort level. Give one of {string.Join(", ", Efforts.Select(e => e.Value))}.");

    /// <summary>
    /// A permission mode from <see cref="Modes"/>, in any case, in its listed spelling (`manual` reads as `default`); or
    /// why it is not one. bypassPermissions and dontAsk are refused with their own reason (see the header).
    /// </summary>
    public static Result<string> Mode(string given) =>
        given.Trim() is var mode && mode.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "default"
        : mode.Equals("bypassPermissions", StringComparison.OrdinalIgnoreCase)
            ? Result.Failure<string>("Deskbar does not start sessions that skip every permission check (bypassPermissions); Claude Code's docs keep that mode for isolated containers and VMs.")
        : mode.Equals("dontAsk", StringComparison.OrdinalIgnoreCase)
            ? Result.Failure<string>("Deskbar does not offer dontAsk for a task session: it denies every command that would ask, the session's own deskbar commands included.")
        : Modes.TryFirst(m => m.Value.Equals(mode, StringComparison.OrdinalIgnoreCase))
            .Map(m => m.Value)
            .ToResult($"'{given}' is not a permission mode. Give one of {string.Join(", ", Modes.Select(m => m.Value))}.");

    /// <summary>
    /// Settings from three typed values, each blank for "not set"; the first value that is not a valid choice fails the
    /// whole, with its reason (for a command a person typed, where a silent drop would hide his mistake).
    /// </summary>
    public static Result<SessionSettings> Of(string? model, string? effort, string? mode) =>
        Checked(model, Model).Bind(m => Checked(effort, Effort).Bind(e => Checked(mode, Mode)
            .Map((Maybe<string> p) => new SessionSettings(m, e, p))));

    static Result<Maybe<string>> Checked(string? given, Func<string, Result<string>> check) =>
        string.IsNullOrWhiteSpace(given) ? Result.Success(Maybe<string>.None) : check(given).Map(value => Maybe.From(value));
}
