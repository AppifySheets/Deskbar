using System.Windows;
using System.Windows.Automation.Peers;

namespace Deskbar.App;

// Why the bar froze for a second at a time on a busy machine (task ea01fe71, "deskbar is super sluggish when
// resources are tough").
//
// MEASURED on Petre's machine (CPU 100%, memory 97%), a 20 second dotnet-trace of the running app: the UI thread was
// busy 8.0 s, and 4.46 s of that was ONE framework method, AutomationPeer.UpdateSubtree, called from
// ContextLayoutManager.fireAutomationEvents after 28 of the 37 layout passes. One call took 1.45 s; a click that
// landed during it waited that long. The next costliest piece of Deskbar's own code took 0.8 s.
//
// What it is: once any UI Automation client on the machine has walked a WPF window (a screen reader, a voice or text
// input service, an app reading other apps' controls), WPF keeps that window's automation peer tree in step with its
// element tree, and it does so by walking the WHOLE tree after every layout pass. Deskbar's always-on surfaces are big
// trees that change all day: the bar rebuilds rows on window events and re-lays out on its 1 s clock, stats and icon
// ticks; the tasks panel and the task windows redraw on every task change. So each tick paid for a full walk, and a
// starved CPU stretched that walk from milliseconds to a second.
//
// The cure: these windows answer UI Automation with the window alone and no children, so UpdateSubtree has nothing to
// walk. The window keeps its own peer, name (its Title) and bounds, so tools that list top-level windows still see it.
// What is given up is reading the bar's buttons and the tasks panel's lines through UI Automation (a screen reader
// would hear the window's name and nothing inside it). Deskbar's own code does not read itself that way (BrowserTabs
// reads BROWSERS), and keyboard text input (TSF, the IME, the language switch) does not go through UI Automation.
// Dialogs (Manage, prompts, quick find, the update offer) keep their full trees: they are small and shown briefly.
//
// Use: override OnCreateAutomationPeer in the window, `=> new QuietWindowPeer(this);`.
sealed class QuietWindowPeer(Window owner) : WindowAutomationPeer(owner)
{
    // No children: the one line that stops WPF walking the window's element tree after every layout pass.
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}
