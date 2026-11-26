using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE PICKER ROW AND THE ANSWER BUTTONS, THE BOARD'S HALF. The board hands the page what it draws and passes its clicks on;
// it decides nothing. The row's words come from ClaudeSessions (it holds Claude Code's latest list, which says whether the
// session runs in a window of its own), a pick and a Cancel go to it, and an answer button goes to AnswerWithOption. A
// switch's progress changes the board and a new list pulses the board's own refresh, so the row follows by itself.
public sealed partial class TasksBoard
{
    /// <summary>The app's ClaudeSessions; none in a copy without Claude, which draws no picker row.</summary>
    internal ClaudeSessions? Sessions { get; set; }

    SessionPickRow? SessionPickOf(Guid id) => Sessions?.PickRow(id);

    Task<Result<SwitchStep>> PickSession(Guid id, SessionSettings settings) =>
        Sessions?.Pick(id, settings) ?? Task.FromResult(Result.Failure<SwitchStep>("Claude sessions are not available."));

    Result CancelPick(Guid id) => Sessions?.CancelPick(id) ?? Result.Failure("Claude sessions are not available.");
}

internal sealed partial record TasksWindowActions
{
    /// <summary>What a task's picker row shows (ClaudeSessions.PickRow). None: no row.</summary>
    public Func<Guid, SessionPickRow?>? SessionPickOf { get; init; }

    /// <summary>A pick from the row's menus (ClaudeSessions.Pick): stored, and the session switched when it can be.</summary>
    public Func<Guid, SessionSettings, Task<Result<SwitchStep>>>? PickSession { get; init; }

    /// <summary>The row's Cancel (ClaudeSessions.CancelPick).</summary>
    public Func<Guid, Result>? CancelPick { get; init; }

    /// <summary>An answer button under the question (TasksBoard.AnswerWithOption), with the list it was drawn from.</summary>
    public Func<Guid, int, IReadOnlyList<string>, Result>? AnswerWithOption { get; init; }
}
