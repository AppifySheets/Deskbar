using System.Reactive;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE TASK PETRE PICKED (Petre: "highlight selected task"; controller ruling 21). A task becomes selected when he
// clicks its line, on the bar or in the tasks window, and both views highlight it. One selection, held by the bar
// (FloatingBar.Selection) and handed to the window's controller by the App, so the two always agree. It lives only in
// memory: nothing is written to tasks.json, since it is where Petre's eye is, not a fact about the task.
//
// It stays through every repaint until he picks another task, or the task is closed or deleted (KeepOnly, called by
// both views on each paint), and then nothing is selected.
public sealed class TaskSelection
{
    readonly Subject<Unit> changed = new();

    public Maybe<Guid> Current { get; private set; } = Maybe<Guid>.None;

    /// <summary>Pulses when the selection changes, so both views redraw their highlight.</summary>
    public IObservable<Unit> Changed => changed;

    public bool IsSelected(Guid id) => Current.HasValue && Current.Value == id;

    public void Select(Guid id)
    {
        if (IsSelected(id)) return;
        Current = id;
        changed.OnNext(Unit.Default);
    }

    public void Clear()
    {
        if (Current.HasNoValue) return;
        Current = Maybe<Guid>.None;
        changed.OnNext(Unit.Default);
    }

    /// <summary>Lets go of a task that is closed or gone; an open task stays selected.</summary>
    public void KeepOnly(TaskBoard board)
    {
        if (Current.HasValue && !board.Tasks.Any(t => t.Id == Current.Value && t.IsOpen)) Clear();
    }
}
