using System.Windows;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// Whether an element sits inside one of the pinned or previous workspace's chips (FloatingBar.WorkspaceJumps.cs).
//
// Tests that find a ROW by the text it contains ignore chip text: the current workspace's titled list ends with chips
// named after OTHER workspaces ("↩ Sparrow"), so without this "the row containing Sparrow" would be two rows.
static class Chips
{
    public static bool Inside(DependencyObject element) =>
        element is FrameworkElement { Tag: FloatingBar.WorkspaceJumpTag }
        || (LogicalTreeHelper.GetParent(element) is { } parent && Inside(parent));
}
