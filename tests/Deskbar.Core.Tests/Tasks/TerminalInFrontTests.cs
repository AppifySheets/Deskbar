using System.Reactive;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// The terminal in front selects its task, also when it came to the front before Deskbar knew the window (task 6bb3dac6).
// The window list is a dictionary the test fills, standing in for WorkspaceManager.KnownWindows behind TaskOfWindow, and
// askAgain stands in for the window events and StateChanged the App merges.
public sealed class TerminalInFrontTests
{
    const nint Terminal = 0x12060D04;
    const nint Browser = 0x6040602;
    static readonly Guid TaskId = Guid.NewGuid();
    static readonly Guid Other = Guid.NewGuid();

    readonly Subject<nint> foreground = new();
    readonly Subject<Unit> askAgain = new();
    readonly Dictionary<nint, Guid> known = new();
    readonly List<Guid> selected = [];
    readonly List<string> traced = [];
    bool throwOnAsk;

    public TerminalInFrontTests() =>
        TerminalInFront.Tasks(foreground, askAgain, TaskOf, traced.Add).Subscribe(selected.Add);

    Maybe<Guid> TaskOf(nint hwnd) =>
        throwOnAsk ? throw new InvalidOperationException("window list changed")
            : known.TryGetValue(hwnd, out var id) ? id : Maybe<Guid>.None;

    [Fact]
    public void A_known_terminal_coming_to_the_front_selects_its_task()
    {
        known[Terminal] = TaskId;
        foreground.OnNext(Terminal);
        Assert.Equal([TaskId], selected);
    }

    [Fact]
    public void A_terminal_in_front_before_it_is_known_selects_its_task_once_it_is()
    {
        // The order measured on 2026-11-26: the foreground event first, the window's arrival 0.3 s later.
        foreground.OnNext(Terminal);
        Assert.Empty(selected);

        known[Terminal] = TaskId;
        askAgain.OnNext(Unit.Default);
        Assert.Equal([TaskId], selected);
    }

    [Fact]
    public void A_window_answers_once_per_stint_in_front()
    {
        // A click on another task while the terminal keeps the keyboard must stay: a later re-ask does not take it back.
        known[Terminal] = TaskId;
        foreground.OnNext(Terminal);
        askAgain.OnNext(Unit.Default);
        askAgain.OnNext(Unit.Default);
        Assert.Equal([TaskId], selected);
    }

    [Fact]
    public void Coming_back_to_the_front_selects_again()
    {
        known[Terminal] = TaskId;
        foreground.OnNext(Terminal);
        foreground.OnNext(Browser);
        foreground.OnNext(Terminal);
        Assert.Equal([TaskId, TaskId], selected);
    }

    [Fact]
    public void A_window_learned_after_the_foreground_moved_on_selects_nothing()
    {
        foreground.OnNext(Terminal);
        foreground.OnNext(Browser);
        known[Terminal] = TaskId;
        askAgain.OnNext(Unit.Default);
        Assert.Empty(selected);
    }

    [Fact]
    public void A_re_ask_only_asks_about_the_window_in_front()
    {
        foreground.OnNext(Terminal);
        known[Browser] = Other;
        askAgain.OnNext(Unit.Default);
        Assert.Empty(selected);
    }

    [Fact]
    public void A_failed_ask_is_traced_and_the_next_one_still_answers()
    {
        throwOnAsk = true;
        foreground.OnNext(Terminal);
        Assert.Single(traced);

        throwOnAsk = false;
        known[Terminal] = TaskId;
        askAgain.OnNext(Unit.Default);
        Assert.Equal([TaskId], selected);
    }
}
