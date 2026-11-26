using System.IO;
using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// A bar line whose session waits on a permission question (Allow / Deny from a task's page; FloatingBar.Permission.cs):
// in the Inbox look it wears the shield and its tooltip says what is asked; the line goes back once the
// question is answered. The bar shows; it never answers.
public partial class FloatingBarTaskListTests
{
    [Fact]
    public void A_line_asking_permission_wears_the_shield_and_says_so() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);
        var box = Path.Combine(Path.GetTempPath(), "deskbar-perm-bar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desk = new PermissionDesk(box, () => Start.AddHours(1));
            t.Bar.Permissions = desk;
            Directory.CreateDirectory(Path.Combine(box, "req-1-1"));
            // "s2" is the asking task's session (BarTasks).
            Assert.True(desk.Register("req-1-1", new PermissionRequest("s2", "Bash", @"C:\repos\Example", "default",
                [new PermissionField("command", "git push")])).IsSuccess);
            t.Bar.UpdateLayout();

            Assert.Equal("Asks permission", MarkOf(LineFor(t, k.Asking)));
            // Said in the tooltip, above the line's own text: the bar's lines have no second line.
            Assert.StartsWith("Asks permission: Bash\n", $"{((System.Windows.Controls.Border)LineFor(t, k.Asking).Content).ToolTip}");
            // Nothing on the line can answer it.
            Assert.DoesNotContain(Inside(LineFor(t, k.Asking)).OfType<Button>(), b =>
                System.Windows.Automation.AutomationProperties.GetName(b) is TasksWindow.AllowText or TasksWindow.DenyText);

            Assert.True(desk.Answer("req-1-1", PermissionDecision.Deny).IsSuccess);
            t.Bar.UpdateLayout();
            Assert.Equal("Asks you", MarkOf(LineFor(t, k.Asking)));
        }
        finally
        {
            if (Directory.Exists(box)) Directory.Delete(box, true);
        }
    });
}
