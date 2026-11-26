using System.IO;
using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The permission card in a task's own window (Allow / Deny from a task's page): the window draws the panel's page through
// a TasksWindow of its own, with the same actions, so the card is there too, and its click writes the answer.
public partial class TaskDetailWindowInboxTests
{
    [Fact]
    public void A_task_window_shows_the_card_and_its_click_answers() => StaThread.Run(() =>
    {
        using var t = Build();
        var box = Path.Combine(Path.GetTempPath(), "deskbar-perm-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desk = new PermissionDesk(box, () => Start.AddHours(1));
            t.Board.Permissions = desk;
            var id = t.Tasks.Create("Ship the fix", t.Sparrow, Session("s1")).Value.Id;
            var (window, page) = Open(t, id);
            Directory.CreateDirectory(Path.Combine(box, "req-1-1"));
            Assert.True(desk.Register("req-1-1", new PermissionRequest("s1", "Bash", @"C:\repos\TaskSpaces", "default",
                [new PermissionField("command", "git push fork feature/x")])).IsSuccess);

            var card = Inside(window).OfType<System.Windows.Controls.Border>().Single(b => b.Tag as string == TasksWindow.PermissionCardTag);
            Assert.Contains(Inside(card).OfType<TextBlock>(), b => b.Text == "git push fork feature/x");
            Click(card, TasksWindow.DenyText);

            Assert.EndsWith(" deny\n", File.ReadAllText(Path.Combine(box, "req-1-1", PermissionDesk.AnswerFile)));
            Assert.DoesNotContain(Inside(window).OfType<System.Windows.Controls.Border>(), b => b.Tag as string == TasksWindow.PermissionCardTag);
            Assert.NotNull(page);
        }
        finally
        {
            if (Directory.Exists(box)) Directory.Delete(box, true);
        }
    });
}
