using System.Windows;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// "Copy link" on a task line's menu, the bar's and the panel's (task 5016d533: "create a link to the task and include
// it in github pr / issues / elsewhere so that when i click on it, it opens the task in deskbar").
//
// What lands on the clipboard is the markdown link, [Deskbar task 5016d533](https://deskbar.app/task#<id>), the form a
// pull request, an issue or a comment shows as a short clickable name, and the one `deskbar task link <id>` prints. In
// a place that takes plain text the URL is still whole inside it. TaskLink has why the URL is the web form.
static class TaskLinkClipboard
{
    public const string CopyLinkText = "Copy link";

    // The clipboard is shared with every program and can be held open by one of them for a moment; a copy that finds
    // it busy is traced and dropped rather than thrown on the UI thread.
    public static void Copy(Guid id) =>
        Result.Try(() => Clipboard.SetText(TaskLink.Markdown(id)))
            .TapError(error => ClickTrace.Write($"copy task link: {error}"));
}
