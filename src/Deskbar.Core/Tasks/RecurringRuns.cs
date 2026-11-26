using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Recurring tasks (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md). A repeating task
// starts a quiet Claude session each time its NextRun passes. This file holds what the board and the sweep both need to
// agree on: the two questions a task asks when a run cannot start (and how to recognise them, so setting the repeat
// again can answer them), and the note left on the thread when a run is skipped. Task 4 adds the sweep itself here.
public static class RecurringRuns
{
    /// <summary>Asked when the folder a repeat's runs start in no longer exists.</summary>
    public const string FolderGoneQuestion = "This task repeats, but the folder its runs start in is gone. Set the repeat again with a folder (Repeat in its menu, or task repeat <id> <schedule> --folder <path>).";

    /// <summary>Asked when a run could not be started; the thread carries what Claude Code said.</summary>
    public const string StartFailedQuestion = "This task repeats, but its last run could not start; what Claude Code said is in the thread. Set the repeat again, or wake it, once it can run.";

    /// <summary>
    /// Whether a question is one of the two above. SetRepeat uses it: setting the repeat again is the answer to either, so
    /// a task waiting on one goes back to Backlog. Any other question is Petre's to answer and stays.
    /// </summary>
    public static bool IsRepeatQuestion(Maybe<string> question) =>
        question.HasValue && question.Value is FolderGoneQuestion or StartFailedQuestion;

    /// <summary>The thread note for an occurrence skipped because the repeat's folder is gone.</summary>
    public static string FolderGoneNote(string folder) => $"Scheduled run skipped: {folder} no longer exists.";
}
