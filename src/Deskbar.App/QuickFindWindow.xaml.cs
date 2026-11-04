using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Core.QuickFind;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// What the window needs to draw a group and its rows, resolved by the controller (QuickFind) so this window
// never looks anything up: the header's text and colour, each row's icon, title and the dim detail on its
// right (the browser window a tab is in, or "not running"). Faded marks what would have to be started.
public sealed record FindRowView(string Key, ImageSource? Icon, string Title, string? Detail);

public sealed record FindGroupView(string Header, string? Colour, bool Faded, IReadOnlyList<FindRowView> Rows);

// QUICK FIND'S WINDOW. Display only: it draws groups of rows, keeps one row highlighted and reports what was
// typed, chosen or dismissed. What is listed and what Enter does belong to QuickFind.
//
// The highlight is kept by the row's KEY across every redraw (FindHighlight), because the list refreshes while
// the box is open and Enter must land on the row Petre is looking at, not on whatever moved into its place.
public partial class QuickFindWindow : Window
{
    static readonly Brush HighlightFill = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    static readonly Brush Text = Frozen(Colors.White);
    static readonly Brush DimText = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    static readonly Brush NoColour = Frozen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    readonly Subject<string> typed = new();
    readonly Subject<string> chosen = new();
    readonly Subject<Unit> dismissed = new();
    readonly Dictionary<string, Border> rowsByKey = [];
    IReadOnlyList<string> pieces = [];

    public QuickFindWindow()
    {
        InitializeComponent();
        Query.TextChanged += (_, _) => typed.OnNext(Query.Text);
        PreviewKeyDown += (_, e) => e.Handled = Press(e.Key);
        // Clicking anywhere else closes it, like the Start menu: a finder left open behind the app you went to
        // would only be in the way.
        Deactivated += (_, _) => Dismiss();
    }

    public IObservable<string> Typed => typed.AsObservable();
    public IObservable<string> Chosen => chosen.AsObservable();
    public IObservable<Unit> Dismissed => dismissed.AsObservable();

    // Rows in display order, which is the order Up and Down walk. For the controller and the tests.
    public IReadOnlyList<string> Keys { get; private set; } = [];
    public IReadOnlyList<string> Headers { get; private set; } = [];
    public string? Highlighted { get; private set; }
    public bool IsOpen { get; private set; }

    // Opens on the screen Petre is working on: the one holding the foreground window, read BEFORE this window
    // takes the foreground. Centred across, a quarter of the way down, the way launchers usually sit.
    public void Open(string text)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        Query.Text = text;
        Query.SelectAll();
        IsOpen = true;
        Show();
        PlaceOn(foreground);
        Activate();
        Query.Focus();
    }

    public void Dismiss()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Hide();
        dismissed.OnNext(Unit.Default);
    }

    // Typed pieces come along so each title can show which letters matched.
    public void Present(IReadOnlyList<FindGroupView> groups, IReadOnlyList<string> typedPieces)
    {
        pieces = typedPieces;
        Rows.Children.Clear();
        rowsByKey.Clear();
        groups.ToList().ForEach(group =>
        {
            Rows.Children.Add(Header(group));
            group.Rows.ToList().ForEach(row =>
            {
                var element = Row(row, group.Faded);
                rowsByKey[row.Key] = element;
                Rows.Children.Add(element);
            });
        });
        Keys = groups.SelectMany(g => g.Rows).Select(r => r.Key).ToList();
        Headers = groups.Select(g => g.Header).ToList();
        Count.Text = Keys.Count switch { 0 => typedPieces.Count == 0 ? "" : "nothing", 1 => "1 result", var n => $"{n} results" };
        Highlight(FindHighlight.Keep(Keys, Highlighted));
    }

    // Keyboard handling, as one method so the tests can drive it without a real key event.
    public bool Press(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                Dismiss();
                return true;
            case Key.Enter:
                if (Highlighted is { } key0) chosen.OnNext(key0);
                return true;
            case Key.Down:
                Highlight(FindHighlight.Move(Keys, Highlighted, +1));
                return true;
            case Key.Up:
                Highlight(FindHighlight.Move(Keys, Highlighted, -1));
                return true;
            default:
                return false;
        }
    }

    void Highlight(string? key)
    {
        Highlighted = key;
        rowsByKey.ToList().ForEach(pair => pair.Value.Background = pair.Key == key ? HighlightFill : Brushes.Transparent);
        if (key is not null && rowsByKey.TryGetValue(key, out var row)) row.BringIntoView();
    }

    UIElement Header(FindGroupView group)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 2), Opacity = group.Faded ? 0.55 : 1 };
        panel.Children.Add(new Border
        {
            Width = 3, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0),
            Background = group.Colour is { } colour ? Frozen((Color)ColorConverter.ConvertFromString(colour)) : NoColour,
        });
        panel.Children.Add(new TextBlock { Text = group.Header, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Text });
        return panel;
    }

    Border Row(FindRowView row, bool faded)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Source = row.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 9, 0) };
        var title = new TextBlock { Foreground = Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Emphasise(row.Title, pieces).ToList().ForEach(title.Inlines.Add);
        Grid.SetColumn(title, 1);
        grid.Children.Add(icon);
        grid.Children.Add(title);

        if (row.Detail is { } detail)
        {
            var right = new TextBlock
            {
                Text = detail, Foreground = DimText, FontSize = 11, MaxWidth = 180, Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var border = new Border { Child = grid, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Opacity = faded ? 0.55 : 1, Cursor = Cursors.Hand };
        border.MouseLeftButtonUp += (_, _) => chosen.OnNext(row.Key);
        return border;
    }

    // The title with every occurrence of every typed piece in bold, so Petre can see why a row matched (the
    // match can also be in the workspace or the window a tab is in, which the header and detail show).
    internal static IEnumerable<Inline> Emphasise(string title, IReadOnlyList<string> typedPieces)
    {
        var bold = new bool[title.Length];
        typedPieces.Where(p => p.Length > 0).ToList().ForEach(piece =>
        {
            for (var at = title.IndexOf(piece, StringComparison.OrdinalIgnoreCase); at >= 0; at = title.IndexOf(piece, at + 1, StringComparison.OrdinalIgnoreCase))
                Array.Fill(bold, true, at, piece.Length);
        });

        var start = 0;
        for (var i = 1; i <= title.Length; i++)
        {
            if (i < title.Length && bold[i] == bold[start]) continue;
            var run = new Run(title[start..i]);
            if (bold[start]) run.FontWeight = FontWeights.Bold;
            yield return run;
            start = i;
        }
    }

    // Centred on the work area of the screen holding `anchor`, in that screen's own DPI, for the reason
    // WorkspaceSwitcher.PlaceBeside gives: a window-scoped DPI query can be stale right after Show().
    void PlaceOn(nint anchor)
    {
        var monitor = NativeMethods.MonitorFromWindow(anchor != 0 ? anchor : new WindowInteropHelper(this).Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var (scaleX, scaleY) = (dpiX / 96.0, dpiY / 96.0);
        var (left, top) = (info.rcWork.Left / scaleX, info.rcWork.Top / scaleY);
        var (width, height) = ((info.rcWork.Right - info.rcWork.Left) / scaleX, (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
        Left = left + Math.Max(0, (width - ActualWidth) / 2);
        Top = top + height / 4;
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
