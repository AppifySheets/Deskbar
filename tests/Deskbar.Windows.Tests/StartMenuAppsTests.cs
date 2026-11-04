using System.IO;
using Deskbar.Windows.Shell;

namespace Deskbar.Windows.Tests;

// Reads the Start menu's full app list (shell:AppsFolder), as quick find's launch rows do. Reads only; never
// starts anything. Every Windows install lists at least its own built-in apps.
public class StartMenuAppsTests
{
    [Fact]
    public void Reads_apps_with_a_name_and_a_parsing_name()
    {
        var apps = StartMenuApps.Read();

        Assert.NotEmpty(apps);
        Assert.All(apps, app => Assert.False(string.IsNullOrWhiteSpace(app.Name)));
        Assert.All(apps, app => Assert.False(string.IsNullOrWhiteSpace(app.ParsingName)));
    }

    // A packaged app has an AppUserModelID and no target; a desktop app's target, when there is one, is a path.
    [Fact]
    public void A_target_when_present_is_a_rooted_path() =>
        Assert.All(StartMenuApps.Read().Where(a => a.TargetPath is not null), app => Assert.True(Path.IsPathRooted(app.TargetPath)));
}
