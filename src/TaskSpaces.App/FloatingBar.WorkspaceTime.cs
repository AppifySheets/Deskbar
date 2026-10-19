using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskSpaces.App;

// TODAY'S TIME BESIDE EACH WORKSPACE'S NAME. Petre: "where are my elapsed times?", then "i want it next to
// workspace titles". The time was tracked all along (#53) but only shown on Manage's Time tab.
//
// The number is the Time tab's own "today" column, read through TimeToday, which the composition root points
// at the same TimeTracker. So the caption and the tab cannot disagree, and the bar holds no clock of its own.
//
// Drawn as a dim " · 2 h 11 min" after the name, in a text of its own like the " · empty" mark, so the name's
// Text stays the bare name (screen readers, and everything that finds a row by name, read it). It is docked
// so that a long name is the thing that gets the ellipsis, and the time stays readable.
//
// REFRESHED IN PLACE once a minute, like the aging icons. Time accrues every 15 seconds whether or not the
// bar has anything to redraw, and a rebuild a minute just to move a number would cost the bar its clicks
// (a rebuild that lands mid-click can lose one; see ClickTrace). So every caption's time text is kept, with
// its workspace, from the rebuild that made it, and the timer rewrites only those.
public partial class FloatingBar
{
    // Today's tracked time for a workspace id. Set by the composition root; left null, as the tests but these
    // leave it, captions carry no time.
    //
    // Setting it redraws the rows once. The constructor has already drawn them by the time an object
    // initializer assigns this, so without the redraw the first paint would carry no times until some window
    // event happened to rebuild the bar.
    public Func<Guid, TimeSpan>? TimeToday
    {
        get => timeToday;
        init
        {
            timeToday = value;
            if (value is not null) Rebuild();
        }
    }

    Func<Guid, TimeSpan>? timeToday;

    // Quieter than the name, louder than " · empty", which is a fact you rarely need; this one you glance at.
    static readonly Brush TimeMarkForeground = Frozen(0x8C, 0xFF, 0xFF, 0xFF);

    // `Empty`: the row's own " · empty" mark when it has one, which gives way to the time (see FillTimeMark).
    readonly List<(Guid Workspace, TextBlock Mark, UIElement? Empty)> timeMarks = [];
    readonly DispatcherTimer timeMarkTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    void StartWorkspaceTimes()
    {
        timeMarkTimer.Tick += (_, _) => RefreshWorkspaceTimes();
        timeMarkTimer.Start();
        Closed += (_, _) => timeMarkTimer.Stop();
    }

    // Emptied at the start of every rebuild, which replaces every caption.
    void ForgetWorkspaceTimes() => timeMarks.Clear();

    // "45 min", "1 h", "2 h 11 min", or nothing under a minute: "0 min" on every row you have not been to
    // today would be noise on the rows you most need to scan past.
    internal static string? TimeMark(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1) ? null
        : time < TimeSpan.FromHours(1) ? $"{(int)time.TotalMinutes} min"
        : time.Minutes == 0 ? $"{(int)time.TotalHours} h"
        : $"{(int)time.TotalHours} h {time.Minutes} min";

    // The mark for one caption, registered for the minute refresh. Always built for a workspace row, even
    // with nothing to say yet, so the first minute in a workspace appears without waiting for a rebuild.
    // Null for a row that is not a workspace (or when no clock is wired), which then has no mark at all.
    // `empty`: the row's " · empty" mark, if it is an empty workspace, so the two can swap as the time arrives.
    TextBlock? WorkspaceTimeMark(Guid? workspace, double fontSize, double? lineHeight, UIElement? empty = null)
    {
        if (TimeToday is null || workspace is not { } id) return null;
        var mark = new TextBlock
        {
            FontSize = fontSize,
            Foreground = TimeMarkForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        if (lineHeight is { } line)
        {
            mark.LineHeight = line;
            mark.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        timeMarks.Add((id, mark, empty));
        FillTimeMark(mark, TimeToday(id), empty);
        return mark;
    }

    // Every caption's time, read again. Internal so a test can drive it without waiting a minute.
    internal void RefreshWorkspaceTimes()
    {
        if (TimeToday is not { } today) return;
        timeMarks.ForEach(m => FillTimeMark(m.Mark, today(m.Workspace), m.Empty));
    }

    // ONE MARK, NOT TWO. Petre, on "Mess… · empty · 1 min": "empty is extra there". A workspace with time today
    // is one you work in, which is what "empty" was there to hint at for a row with nothing on it; both together
    // also pushed the name into an ellipsis. So when the time shows, " · empty" hides, and it comes back only
    // for an empty workspace you have not used today. Done here rather than when the row is built, so a row
    // that gains its first minute drops "empty" on the refresh instead of at the next rebuild.
    static void FillTimeMark(TextBlock mark, TimeSpan time, UIElement? empty)
    {
        var text = TimeMark(time);
        mark.Text = text is null ? "" : $" · {text}";
        mark.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (empty is not null) empty.Visibility = text is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
