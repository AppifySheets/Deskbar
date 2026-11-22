using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Deskbar.App;

// MARKDOWN FOR THE COMMENT THREADS, as a WPF FlowDocument. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "Comments" (a comment's text is markdown, rendered) and "Left to the builder" (which subset, and how; no WebView).
// No markdown package either (global constraints: no new NuGet package unless a task says so), so this is a small
// line-based reader of the subset Claude sessions and Petre actually write in a comment:
//
//   paragraphs        split by a blank line; a single newline inside one is a line break, as in a GitHub comment,
//                     because a session's comment is written line by line and joining its lines would lose that
//   **bold**, *italic*
//   `inline code`     nothing inside it is read as markdown
//   ```fenced code``` kept line for line in a monospace block; the language word after the fence is dropped
//   [text](url)       and bare http(s) addresses: links. ONLY web addresses become links. A comment may come from a
//                     session, and a click must never run a program, so file:, ms-settings: and every other scheme
//                     stay the text they were written as.
//   - item / * item   a bullet list; 1. item, a numbered list starting at its first number
//
// Anything else (headings, quotes, tables, an unclosed **) shows as the text it is, in a paragraph: a comment is never
// lost to a construct this does not know.
//
// A link opens through `open`, which the window hands in; left null it opens the way `deskbar.cmd open` opens a link
// "as usual" (RemoteControl.Open's last branch): the shell hands it to the default browser, on the desktop in front.
// That is the right branch here, because Petre clicked it on the desktop he is looking at; the background-workspace
// branch of `open` is for a session working somewhere else.
internal static class MarkdownBlocks
{
    /// <summary>The Tag of an inline code run, for the tests and the window's styling.</summary>
    internal const string CodeTag = "code";

    /// <summary>The Tag of a fenced code block's paragraph.</summary>
    internal const string CodeBlockTag = "code-block";

    static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    static readonly FontFamily TextFont = new("Segoe UI");
    static readonly Brush CodeFill = Frozen(Color.FromRgb(0x2A, 0x2C, 0x33));
    static readonly Brush LinkInk = Frozen(Color.FromRgb(0x7F, 0xB2, 0xFF));

    // A list item: a dash, star or plus then a space; or a number with a full stop or a bracket then a space.
    static readonly Regex Bullet = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex Numbered = new(@"^\s*(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);

    // The inline constructs, tried left to right, the first match winning at each place: code first, so nothing
    // inside it is read; then bold before italic, so ** is not read as two empty italics; then links, then a bare
    // address. A bare address stops before closing punctuation, so the full stop ending a sentence is not in the link.
    static readonly Regex Inline = new(
        @"(?<code>`[^`\n]+`)" +
        @"|(?<bold>\*\*(?=\S)(?<boldText>.+?)(?<=\S)\*\*)" +
        @"|(?<italic>\*(?=[^\s*])(?<italicText>.+?)(?<=[^\s*])\*)" +
        @"|(?<link>\[(?<linkText>[^\]\n]+)\]\((?<linkUrl>[^)\s]+)\))" +
        @"|(?<bare>https?://[^\s<>()\[\]]*[^\s<>()\[\].,;:!?'""])",
        RegexOptions.Compiled);

    /// <summary>
    /// The comment as a document, ready for a read-only RichTextBox. <paramref name="open"/> is what a click on a link
    /// does; left null, the default browser opens it (see the header).
    /// </summary>
    public static FlowDocument Render(string markdown, Action<Uri>? open = null)
    {
        var opener = open ?? OpenInBrowser;
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = TextFont, TextAlignment = TextAlignment.Left };
        Blocks((markdown ?? "").Replace("\r\n", "\n").Split('\n'), opener).ToList().ForEach(document.Blocks.Add);
        return document;
    }

    // --- blocks --------------------------------------------------------------------------------------------------

    // The lines as blocks, read top to bottom. Each step takes one block's lines off the front and passes the rest on,
    // so the reader is a fold over the lines without an index to keep in step.
    static IEnumerable<Block> Blocks(IReadOnlyList<string> lines, Action<Uri> open)
    {
        var rest = lines;
        while (rest.Count > 0)
        {
            var (block, taken) = Next(rest, open);
            if (block is not null) yield return block;
            rest = rest.Skip(taken).ToArray();
        }
    }

    // The block starting at the first line, and how many lines it took. A blank line takes one line and makes nothing.
    static (Block? Block, int Taken) Next(IReadOnlyList<string> lines, Action<Uri> open)
    {
        var first = lines[0];
        if (string.IsNullOrWhiteSpace(first)) return (null, 1);
        if (first.TrimStart().StartsWith("```", StringComparison.Ordinal)) return Fenced(lines);
        if (Bullet.IsMatch(first)) return ListOf(lines, Bullet, TextMarkerStyle.Disc, open);
        if (Numbered.IsMatch(first)) return ListOf(lines, Numbered, TextMarkerStyle.Decimal, open);

        // A paragraph runs to the next blank line, fence or list item.
        var taken = lines.TakeWhile((line, i) => i == 0 || !EndsParagraph(line)).ToArray();
        return (ParagraphOf(taken, open), taken.Length);
    }

    static bool EndsParagraph(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("```", StringComparison.Ordinal) || Bullet.IsMatch(line) || Numbered.IsMatch(line);

    // Lines joined by line breaks, each read for inline markdown.
    static Paragraph ParagraphOf(IReadOnlyList<string> lines, Action<Uri> open)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
        lines.Select((line, i) => (line, i)).ToList().ForEach(x =>
        {
            if (x.i > 0) paragraph.Inlines.Add(new LineBreak());
            Inlines(x.line.TrimEnd(), open).ToList().ForEach(paragraph.Inlines.Add);
        });
        return paragraph;
    }

    // A fenced block, from its opening fence to the closing one (or to the end, when the comment forgot to close it).
    // Its lines are kept exactly, leading spaces included, and read for nothing.
    static (Block, int) Fenced(IReadOnlyList<string> lines)
    {
        var body = lines.Skip(1).TakeWhile(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)).ToArray();
        var closed = body.Length + 1 < lines.Count;
        var code = new Paragraph
        {
            Tag = CodeBlockTag,
            FontFamily = CodeFont,
            Background = CodeFill,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 2, 0, 6),
        };
        body.Select((line, i) => (line, i)).ToList().ForEach(x =>
        {
            if (x.i > 0) code.Inlines.Add(new LineBreak());
            code.Inlines.Add(new Run(x.line));
        });
        return (code, body.Length + (closed ? 2 : 1));
    }

    // Consecutive items of one kind of list. An indented line that is not an item continues the item above it, after
    // a line break, so a wrapped item stays one item.
    static (Block, int) ListOf(IReadOnlyList<string> lines, Regex item, TextMarkerStyle marker, Action<Uri> open)
    {
        var taken = lines.TakeWhile((line, i) => i == 0 || item.IsMatch(line) || (line.StartsWith(' ') && !string.IsNullOrWhiteSpace(line) && !EndsParagraph(line))).ToArray();
        var items = taken.Aggregate(new List<List<string>>(), (all, line) =>
        {
            var match = item.Match(line);
            if (match.Success) all.Add([match.Groups[match.Groups.Count - 1].Value]);
            else all[^1].Add(line.Trim());
            return all;
        });
        var list = new List { MarkerStyle = marker, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(18, 0, 0, 0) };
        if (marker == TextMarkerStyle.Decimal && int.TryParse(Numbered.Match(lines[0]).Groups[1].Value, out var start)) list.StartIndex = start;
        items.ForEach(lines0 =>
        {
            var paragraph = ParagraphOf(lines0, open);
            paragraph.Margin = new Thickness(0);
            list.ListItems.Add(new ListItem(paragraph));
        });
        return (list, taken.Length);
    }

    // --- inlines -------------------------------------------------------------------------------------------------

    // The text between matches is plain; each match becomes its construct, bold and italic read again inside.
    static IEnumerable<Inline> Inlines(string text, Action<Uri> open)
    {
        var at = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > at) yield return new Run(text[at..match.Index]);
            yield return Construct(match, open);
            at = match.Index + match.Length;
        }
        if (at < text.Length) yield return new Run(text[at..]);
    }

    static Inline Construct(Match match, Action<Uri> open)
    {
        if (match.Groups["code"].Success)
            return new Run(match.Value[1..^1]) { Tag = CodeTag, FontFamily = CodeFont, Background = CodeFill };
        if (match.Groups["bold"].Success)
            return new Bold().With(Inlines(match.Groups["boldText"].Value, open));
        if (match.Groups["italic"].Success)
            return new Italic().With(Inlines(match.Groups["italicText"].Value, open));
        if (match.Groups["link"].Success)
            return WebAddress(match.Groups["linkUrl"].Value) is { } uri
                ? Link(Inlines(match.Groups["linkText"].Value, open), uri, open)
                : new Run(match.Value);
        return WebAddress(match.Value) is { } bare ? Link([new Run(match.Value)], bare, open) : new Run(match.Value);
    }

    // A hyperlink whose click goes to the opener. Click rather than RequestNavigate: there is no navigation host in a
    // RichTextBox, and Click is raised on a press in a read-only box with IsDocumentEnabled set.
    static Hyperlink Link(IEnumerable<Inline> content, Uri uri, Action<Uri> open)
    {
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.ToString(), Foreground = LinkInk }.With(content);
        link.Click += (_, e) =>
        {
            e.Handled = true;
            open(uri);
        };
        return link;
    }

    // Only absolute http and https addresses are links (see the header).
    static Uri? WebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;

    static T With<T>(this T span, IEnumerable<Inline> inlines) where T : Span
    {
        inlines.ToList().ForEach(span.Inlines.Add);
        return span;
    }

    // The default opener: the shell hands the address to the default browser. A refusal (no browser registered) is
    // traced, never thrown: a click on a comment must not take the app down.
    static void OpenInBrowser(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClickTrace.Write($"tasks window: could not open {uri}: {e.Message}");
        }
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
