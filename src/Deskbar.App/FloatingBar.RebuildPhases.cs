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
//
// The phases name a ROW; the costs below name WHAT IN EVERY ROW was slow. Petre: "when activating a bar in deskbar,
// it takes a little while to activate", and the trace put the wait in the rebuild after a switch, 142ms of draw on
// average over a day. A rebuild of the same bar in the test harness, with no real windows behind it, draws in 33ms,
// so the other hundred is paid to the OS from inside the draw, and the per-row phases cannot say by whom. So the
// draw's suspects (the icon lookup, the marker, the menus) are each clocked across the whole rebuild, and the
// slow line lists any that reached 20ms in total. Same switch, same cost when off: Cost without a clock is a call.
public partial class FloatingBar
{
    System.Diagnostics.Stopwatch? phaseClock;
    readonly List<(string Name, long Ms)> phases = [];
    readonly Dictionary<string, long> costs = [];

    void StartPhases()
    {
        phases.Clear();
        costs.Clear();
        phaseClock = ClickTrace.On ? System.Diagnostics.Stopwatch.StartNew() : null;
    }

    // Runs `make` and adds what it took to the named cost, summed over the rebuild. Only while a rebuild is being
    // clocked, so a lookup made outside one (a hover, a click) is neither timed nor counted.
    T Cost<T>(string name, Func<T> make)
    {
        if (phaseClock is null) return make();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var made = make();
        costs[name] = costs.GetValueOrDefault(name) + System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks;
        return made;
    }

    void Cost(string name, Action run) => Cost(name, () => { run(); return 0; });

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
        var slow = phases.Where(p => p.Ms >= 20).Select(p => $"{p.Name} {p.Ms}ms")
            .Concat(costs.Select(c => (c.Key, Ms: TimeSpan.FromTicks(c.Value).TotalMilliseconds))
                .Where(c => c.Ms >= 20).Select(c => $"{c.Key} {c.Ms:F0}ms in all"))
            .ToList();
        return slow.Count == 0 ? "" : $" slow: {string.Join(", ", slow)}";
    }
}
