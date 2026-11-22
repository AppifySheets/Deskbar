using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Reporting;

namespace Deskbar.App;

/// <summary>
/// What the report window needs from the rest of the app, handed in so the tests can run the window with no network,
/// no disk and no Notepad (ReportService.Live builds the real one).
/// </summary>
public sealed record ReportServices(
    Func<ProblemReport, IProgress<ReportProgress>?, CancellationToken, Task<Result<int>>> Send,
    Func<ProblemReport, Result<string>> Archive,
    Action<string> Forget,
    Action<string> OpenFolder,
    Action<string> ViewDump,
    string AppVersion,
    string WindowsBuild,
    bool Store,
    bool Pro);

// "Report a problem…", on the tray menu and the bar's background menu (issue #331). It files a report without the
// user signing in anywhere: the text, up to five screenshots, the debug dump and an optional contact line go to the
// relay at report.deskbar.app, which files them as an issue in the PRIVATE repo AppifySheets/Deskbar-reports.
//
// THE DUMP is composed by the caller when the window opens, the moment of the problem, and kept as text. View writes
// that same text and opens it, and Send sends it, so what the user reads is what goes. It lists every window title
// on the user's screens, which is why the box is there and why View is.
//
// SCREENSHOTS: Ctrl+V takes the clipboard's picture (Win+Shift+S, Print Screen, a copied image) or copied image
// files; dropping image files or a picture works too. Ctrl+V with only text on the clipboard is left to the text box
// that has focus. ReportImages makes each one a PNG or JPEG under the relay's limit.
//
// SENDING is one upload per file and then the report (ReportClient), with the progress line counting them. The
// report id is made once per window, so Try again after a failure resends the same report and the files already up
// count as done. A failure saves the whole report under %APPDATA%\Deskbar\reports and names the folder; a later
// success deletes that copy again.
public partial class ReportWindow : Window
{
    readonly string dump;
    readonly ReportServices services;
    readonly string reportId = ProblemReport.NewId();

    ImmutableList<ReportImages.Shot> shots = [];
    // The report as first sent, kept so Try again sends exactly the same thing under the same id.
    ProblemReport? sent;
    // Where the report was saved after a failed send; deleted again once a retry gets through.
    string? archived;
    CancellationTokenSource? sending;
    bool closed;

    // The state panel's three looks. Frozen, because a WPF brush that is not frozen belongs to the thread that made it.
    static readonly (Brush Border, Brush Fill, Brush Title) Neutral = Look("#ABADB3", "#F0F0F0", "#000000");
    static readonly (Brush Border, Brush Fill, Brush Title) Good = Look("#8FC98F", "#F0F8F0", "#107C10");
    static readonly (Brush Border, Brush Fill, Brush Title) Bad = Look("#E2C08A", "#FDF6EA", "#9D5D00");

    public ReportWindow(string dump, ReportServices services)
    {
        InitializeComponent();
        this.dump = dump;
        this.services = services;

        // Send waits for something in "What happened?": a report with only a screenshot has no title and no story.
        Description.TextChanged += (_, _) => SendButton.IsEnabled = Description.Text.Trim().Length > 0;
        Contact.TextChanged += (_, _) => ContactHint.Visibility = Contact.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        Loaded += (_, _) => Description.Focus();
        // Closing the window mid-send stops the send; its result then has no window to land in.
        Closed += (_, _) => { closed = true; sending?.Cancel(); };
    }

    static (Brush, Brush, Brush) Look(string border, string fill, string title) =>
        (Frozen(border), Frozen(fill), Frozen(title));

    static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal int ShotCount => shots.Count;

    // ---- screenshots ------------------------------------------------------------------------------------------------

    /// <summary>Adds a screenshot, or says why not (five already, or a picture that could not be read).</summary>
    internal void Add(Result<ReportImages.Shot> shot)
    {
        if (shots.Count >= ReportLimits.Screenshots)
        {
            Note($"Up to {ReportLimits.Screenshots} screenshots. Remove one to add another.");
            return;
        }
        shot.Match(
            ok => { shots = shots.Add(ok); Note(null); RenderShots(); },
            error => Note(error));
    }

    void Remove(ReportImages.Shot shot)
    {
        shots = shots.Remove(shot);
        Note(null);
        RenderShots();
    }

    void Note(string? text)
    {
        ShotNote.Text = text ?? "";
        ShotNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The thumbnails in order, then the drop hint while there is room for another.
    void RenderShots()
    {
        Shots.Children.Clear();
        shots.Select(Thumbnail).ToList().ForEach(tile => Shots.Children.Add(tile));
        if (shots.Count < ReportLimits.Screenshots) Shots.Children.Add(DropHint);
    }

    FrameworkElement Thumbnail(ReportImages.Shot shot)
    {
        var remove = new Button
        {
            Content = "✕", FontSize = 9, Width = 16, Height = 16, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0), ToolTip = "Remove this screenshot",
        };
        remove.Click += (_, _) => Remove(shot);
        var tile = new Grid { Width = 92, Height = 58, Margin = new Thickness(0, 0, 6, 6) };
        tile.Children.Add(new Border
        {
            BorderBrush = Neutral.Border, BorderThickness = new Thickness(1),
            Child = new Image { Source = shot.Thumbnail, Stretch = Stretch.UniformToFill },
        });
        tile.Children.Add(remove);
        return tile;
    }

    // Ctrl+V: a picture or image files on the clipboard become screenshots, wherever the focus is. Anything else
    // (plain text) is left alone, so pasting into the text boxes keeps working.
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || Form.Visibility != Visibility.Visible) return;
        e.Handled = PasteImages();
    }

    bool PasteImages()
    {
        // The clipboard is shared with every other program and throws when one of them holds it open.
        var files = Result.Try(() => Clipboard.ContainsFileDropList()
            ? Clipboard.GetFileDropList().Cast<string>().Where(ReportImages.LooksLikeImage).ToList()
            : []).GetValueOrDefault([]);
        if (files.Count > 0)
        {
            files.ForEach(file => Add(ReportImages.FromFile(file)));
            return true;
        }
        var picture = Result.Try(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null).GetValueOrDefault((BitmapSource?)null);
        if (picture is null) return false;
        Add(ReportImages.FromBitmap(picture));
        return true;
    }

    // Only a drag carrying files or a picture is taken over; dragging text inside a text box is left to the box.
    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!CarriesImages(e.Data) || Form.Visibility != Visibility.Visible) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (!CarriesImages(e.Data) || Form.Visibility != Visibility.Visible) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var images = files.Where(ReportImages.LooksLikeImage).ToList();
            if (images.Count == 0) Note("Only pictures can be added: PNG, JPEG, BMP, GIF, TIFF or WebP.");
            images.ForEach(file => Add(ReportImages.FromFile(file)));
        }
        else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource picture)
            Add(ReportImages.FromBitmap(picture));
    }

    static bool CarriesImages(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap);

    // ---- the dump ---------------------------------------------------------------------------------------------------

    void OnViewDump(object sender, RoutedEventArgs e) => services.ViewDump(dump);

    // ---- sending ----------------------------------------------------------------------------------------------------

    /// <summary>The report as the form stands: screenshots in order as shot-1, shot-2..., then the dump if ticked.</summary>
    internal ProblemReport Compose() => new(
        reportId,
        Description.Text.Trim(),
        Contact.Text.Trim(),
        services.AppVersion,
        services.WindowsBuild,
        services.Store,
        services.Pro,
        shots.Select((shot, index) => new ReportAttachment(ReportLimits.ShotName(index + 1, shot.Jpeg), shot.Bytes))
            .Concat(IncludeDump.IsChecked == true
                ? new[] { new ReportAttachment(ReportLimits.DumpName, ReportLimits.DumpBytes(dump)) }
                : Array.Empty<ReportAttachment>())
            .ToList());

    // Send, and Try again after a failure: the same handler, since a retry is the same report sent again.
    async void OnSend(object sender, RoutedEventArgs e)
    {
        sent ??= Compose();
        await SendAsync(sent);
    }

    internal async Task SendAsync(ProblemReport report)
    {
        Form.Visibility = Visibility.Collapsed;
        SendButton.Visibility = Visibility.Collapsed;
        OpenFolderLink.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Cancel";
        ShowState(Neutral, "Sending…", null, null, progress: 0);

        using var cancel = new CancellationTokenSource();
        sending = cancel;
        var progress = new Progress<ReportProgress>(step =>
        {
            if (closed) return;
            StateTitle.Text = $"Sending {step.Step} of {step.Steps} ({step.What})…";
            Progress.Value = 100.0 * (step.Step - 1) / step.Steps;
        });

        // async void sits above this, so nothing may escape it: an unexpected throw becomes a failed send, which
        // keeps the report on disk, rather than an unhandled exception that ends the app.
        Result<int> result;
        try { result = await services.Send(report, progress, cancel.Token); }
        catch (Exception ex) { result = Result.Failure<int>($"Something went wrong while sending: {ex.Message}"); }
        sending = null;
        if (closed) return;

        result.Match(number => Sent(report, number), error => Failed(report, error));
    }

    void Sent(ProblemReport report, int number)
    {
        ClickTrace.Write($"report: sent {report.Id[..8]} as #{number}, {report.Attachments.Count} file(s)");
        if (archived is { } folder) services.Forget(folder);
        archived = null;
        var reply = report.Contact.Length > 0 ? " We will write to you when we know more." : "";
        ShowState(Good, "Sent, thanks.", $"Your reference is #{number}.{reply}", null, progress: null);
        CloseButton.Content = "Close";
        CloseButton.IsDefault = true;
    }

    void Failed(ProblemReport report, string error)
    {
        ClickTrace.Write($"report: {report.Id[..8]} not sent: {error}");
        // Saved once, on the first failure; a retry that fails again keeps pointing at the same folder.
        var saved = archived is { } existing ? Result.Success(existing) : services.Archive(report);
        archived = saved.IsSuccess ? saved.Value : null;
        saved.Match(
            folder => ShowState(Bad, "Could not send.", $"{error} The report is saved here, nothing is lost:", folder, progress: null),
            saveError => ShowState(Bad, "Could not send.", $"{error} {saveError}", null, progress: null));
        OpenFolderLink.Visibility = archived is null ? Visibility.Collapsed : Visibility.Visible;
        SendButton.Content = "Try again";
        SendButton.Visibility = Visibility.Visible;
        SendButton.IsEnabled = true;
        CloseButton.Content = "Close";
    }

    // The state panel: colours, a title, an optional line of text, an optional path, and the progress bar when sending.
    void ShowState((Brush Border, Brush Fill, Brush Title) look, string title, string? text, string? path, double? progress)
    {
        StatePanel.Visibility = Visibility.Visible;
        StatePanel.BorderBrush = look.Border;
        StatePanel.Background = look.Fill;
        StateTitle.Foreground = look.Title;
        StateTitle.Text = title;
        StateText.Text = text ?? "";
        StateText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        StatePath.Text = path ?? "";
        StatePath.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        Progress.Value = progress ?? 0;
        Progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (archived is { } folder && Directory.Exists(folder)) services.OpenFolder(folder);
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    // For the tests: what the state panel says.
    internal string StateSummary => StatePanel.Visibility == Visibility.Visible
        ? string.Join(" | ", new[] { StateTitle.Text, StateText.Text, StatePath.Text }.Where(part => part.Length > 0))
        : "";
}
