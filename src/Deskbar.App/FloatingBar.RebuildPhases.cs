namespace Deskbar.App;

// WHERE A SLOW REBUILD SPENT ITS TIME. Petre: "check if remote desktop manager taking a while to come up made the
// bar stuck", "for a second or two". The trace had the stall, one line of it: "rebuild took 2900ms (query 303ms,
// draw 2597ms)", right after a click on a Remote Desktop Manager window that was still coming up. "Draw" is
// everything but the overview query, which is most of the method, so it named no culprit.
//
// So a rebuild now keeps a clock per phase (preparing, the query, activity, pins, each workspace row, the rest),
// and a rebuild slow enough to be logged at all lists the phases that took 20ms or more, by name. The next stall
// then says which row, and so which window, the bar was waiting on. Trace only: with the switch off, Phase does
// nothing and no clock runs.
public partial class FloatingBar
{
    System.Diagnostics.Stopwatch? phaseClock;
    readonly List<(string Name, long Ms)> phases = [];

    void StartPhases()
    {
        phases.Clear();
        phaseClock = ClickTrace.On ? System.Diagnostics.Stopwatch.StartNew() : null;
    }

    void StopPhases() => phaseClock = null;

    // Closes the phase that has been running since the last mark, under this name.
    void Phase(string name)
    {
        if (phaseClock is not { } clock) return;
        phases.Add((name, clock.ElapsedMilliseconds));
        clock.Restart();
    }

    string SlowPhases()
    {
        var slow = phases.Where(p => p.Ms >= 20).Select(p => $"{p.Name} {p.Ms}ms").ToList();
        return slow.Count == 0 ? "" : $" slow: {string.Join(", ", slow)}";
    }
}
