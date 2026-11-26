using System.Reactive;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// The task whose Claude terminal is in front, each time one comes to the front: the stream the App selects tasks from
/// (task 4846fde1, Petre: "active claude terminal window should highlight the current task in deskbar").
/// </summary>
/// <remarks>
/// <para>
/// Asked when the foreground changes, and asked AGAIN while that window stays in front, each time Deskbar's own window
/// list may have learned something new (task 6bb3dac6, Petre: "opening a claude session from the task doesn't mark the
/// task in the bar as active"). A terminal Deskbar has just opened on a session comes to the front before Deskbar knows
/// the window: measured on his machine on 2026-11-26, the foreground event for a new Windows Terminal window came at
/// 12:25:55.566 and the window's arrival at 12:25:55.907. Asked only at the foreground change, the rule found no such
/// window, returned nothing and never asked again, so the task lit up only once he clicked away and back.
/// </para>
/// <para>
/// What counts as "may have learned something" is the caller's: the App passes the window events that teach the
/// manager a window (Appeared, TitleChanged, Activated) and the manager's StateChanged, which also covers a window the
/// 5 s repair adopts without any window event (about one arrival in nine on Petre's machine, WorkspaceManager's
/// RepairWindowList). A re-ask only ever asks about the window in front, so an event of some other window cannot select
/// that other window's task.
/// </para>
/// <para>
/// One answer per stint in front: once the window has named its task, later re-asks for the same stint never select it
/// again, so a click on another task while the terminal keeps the keyboard stays where he put it. A new foreground change
/// drops the wait for the previous window (Switch), so a window that is learned after he has moved on never takes the
/// selection.
/// </para>
/// <para>
/// The re-asks must reach this rule after the manager has handled them, so that taskOf can find the window. The App
/// subscribes after WorkspaceManager.Start, and an Rx Subject calls its observers in the order they subscribed.
/// </para>
/// </remarks>
public static class TerminalInFront
{
    /// <param name="foreground">Every window that comes to the front, as WindowMonitor.ForegroundChanged reports it.</param>
    /// <param name="askAgain">Every moment the answer for the window in front may have changed.</param>
    /// <param name="taskOf">The task a window is on, if any (ClaudeTaskActions.TaskOfWindow).</param>
    /// <param name="trace">Where a failed ask is written. A failure costs that one ask and never the whole stream:
    /// an error reaching the subscription would end it, and terminal selection would stay dead until a restart.</param>
    public static IObservable<Guid> Tasks(IObservable<nint> foreground, IObservable<Unit> askAgain, Func<nint, Maybe<Guid>> taskOf,
        Action<string>? trace = null) =>
        foreground
            .Select(hwnd => Observable.Return(Unit.Default)
                .Concat(askAgain)
                .Select(_ => Ask(hwnd, taskOf, trace))
                .Where(task => task.HasValue)
                .Select(task => task.Value)
                .Take(1))
            .Switch();

    static Maybe<Guid> Ask(nint hwnd, Func<nint, Maybe<Guid>> taskOf, Action<string>? trace)
    {
        try
        {
            return taskOf(hwnd);
        }
        catch (Exception ex)
        {
            trace?.Invoke($"terminal in front: asking for the task of {hwnd:X} threw {ex.GetType().Name}: {ex.Message}");
            return Maybe<Guid>.None;
        }
    }
}
