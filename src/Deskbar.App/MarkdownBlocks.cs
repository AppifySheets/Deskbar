using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

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
//   [text](url)       and bare http(s) and file:/// addresses: links. Web addresses, and files and folders on a drive
//                     of this machine (task f9f25172, Petre: "when giving file links, they should be clickable too").
//                     A comment may come from a session, and a click must never run a program, so a file link opens
//                     only a document; any other file is shown in Explorer, never started (FileLink in Core has the
//                     rules and the reasons). A share, ms-settings: and every other scheme stay the text they were
//                     written as. An address with spaces goes in angle brackets, [page](<C:\My Files\a.html>), as
//                     markdown has it.
//   - item / * item   a bullet list; 1. item, a numbered list starting at its first number
//   # .. ###### title a heading: one line, semibold, with a little room above it. Every level looks the same, because
//                     the text is 11 or 12 points in a narrow column and sizes there would only jump about. Added
//                     2026-11-23 for task descriptions, which Petre writes as sections under "## ..." lines.
//   | a | b |          a table, as GitHub writes one (task 24813c78: a session's answer in the Inbox chat showed its
//   |---|--:|          table as rows of pipes). A header row, a dashes row with as many cells under it (a colon on the
//   | 1 | 2 |          left, right or both ends aligns the column), then every following line with a pipe in it, up to
//                     a blank line, a pipeless line or another block (TableOf has the details). Cells are read for inline markdown; "\|" is a pipe inside a cell. Columns share
//                     the width by how much text each holds, since a FlowDocument table cannot size a column to its
//                     content. Without the dashes row the lines are a paragraph, as on GitHub.
//
// Anything else (quotes, an unclosed **) shows as the text it is, in a paragraph: a comment is never lost to a
// construct this does not know.
//
// COPY ON A CLICK (task b6af9445, Petre: "when there's a backticked or otherwise quoted command or something in the
// chat, hovering over it should show me copy of the entire command"). In a document from Render, three things copy
// their whole text when pressed, and say so in their hover text, "Click to copy": an `inline code` run, a fenced
// block (all its lines, as written), and words in straight or curly double quotes ("Pull and redeploy", a button a
// session names). The point is the WHOLE command: a long one wraps over two lines of a narrow bubble, and selecting
// it by dragging across the wrap was the chore. The copied text flashes for a moment so the press is seen to have
// done something, because WPF closes the hover text on a press, and a small note under the pointer says "Copied"
// (task f39f5db3, see "The note at the pointer" by Copyable).
//   A press, not a release, for the reason a link opens on one: in a read-only box the editor captures the mouse on
//   the press to start a selection, so the release never reaches the run. Taking the press means a drag that starts
//   on code does not select part of it; a drag from the plain text beside it still does, and the whole command is
//   what is wanted nearly every time.
//   A quote copies its words without the quote marks. It is read only when it holds no other markdown (no backtick,
//   star, bracket or "://"), so a quoted link or bold word keeps being what it was; it stays plain text to look at,
//   only the pointer and the hover text change. Inside a link's text nothing copies, so the link keeps its click.
//   InlinesOf (a task line's question and status line, a TextBlock) copies nothing: a press on a line picks the task.
//   The copy goes through `copy`, which tests hand in so they never touch Petre's clipboard; left null it is the
//   clipboard, and a busy clipboard is traced, never thrown.
//
// A link opens through `open`, which the window hands in; left null it opens the way `deskbar.cmd open` opens a link
// "as usual" (RemoteControl.Open's last branch): the shell hands it to the default browser, on the desktop in front
// (a file link goes by FileLink's plan instead, see OpenFile).
// That is the right branch here, because Petre clicked it on the desktop he is looking at; the background-workspace
// branch of `open` is for a session working somewhere else. The tasks panel and the task window hand in an opener that
// goes to the task's workspace first (task 26f15d70, TasksBoard.OpenLink), so a task's link opens among that task's
// windows; it then opens through this same default.
internal static class MarkdownBlocks
{
    /// <summary>The Tag of an inline code run, for the tests and the window's styling.</summary>
    internal const string CodeTag = "code";

    /// <summary>The Tag of a fenced code block's paragraph.</summary>
    internal const string CodeBlockTag = "code-block";

    /// <summary>The Tag of a heading's paragraph.</summary>
    internal const string HeadingTag = "heading";

    /// <summary>The Tag of a table, and of its header row group (the first row), for the tests and the window's styling.</summary>
    internal const string TableTag = "table";
    internal const string TableHeaderTag = "table-header";

    /// <summary>The Tag of a double-quoted run's words (the quote marks are runs of their own beside it).</summary>
    internal const string QuoteTag = "quote";

    /// <summary>What a copyable run, quote or code block says on hover.</summary>
    internal const string CopyHint = "Click to copy";

    static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    static readonly FontFamily TextFont = new("Segoe UI");
    static readonly Brush CodeFill = Frozen(Color.FromRgb(0x2A, 0x2C, 0x33));
    static readonly Brush LinkInk = Frozen(Color.FromRgb(0x7F, 0xB2, 0xFF));

    // The flash on a copied run or block: a muted green, light enough to read the text through on every dark surface.
    static readonly Brush CopiedFill = Frozen(Color.FromRgb(0x2E, 0x6B, 0x45));
    static readonly TimeSpan CopiedFlash = TimeSpan.FromMilliseconds(600);

    /// <summary>The note at the pointer after a copy (see "The note at the pointer").</summary>
    internal const string CopiedNote = "Copied";

    /// <summary>The note when the clipboard was busy and nothing was copied.</summary>
    internal const string NotCopiedNote = "Not copied: the clipboard is busy";

    // How long the note stands: long enough to read one word without looking for it, short enough to be gone before
    // the next thing is read.
    static readonly TimeSpan NoteTime = TimeSpan.FromMilliseconds(1200);

    // A table's grid lines and its header row's fill: white at low strength, so they show on every dark surface the
    // document lands on (a chat bubble, the tasks panel, a task window) without a colour of their own.
    static readonly Brush TableRule = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    static readonly Brush TableHeaderFill = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

    // A list item: a dash, star or plus then a space; or a number with a full stop or a bracket then a space.
    static readonly Regex Bullet = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex Numbered = new(@"^\s*(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);

    // A heading: up to three spaces, one to six hashes, a space, then its text; trailing hashes (the closing kind,
    // "## Title ##") are dropped. "#tag" with no space is not a heading, as on GitHub.
    static readonly Regex Heading = new(@"^ {0,3}#{1,6}\s+(.*?)(?:\s+#+)?\s*$", RegexOptions.Compiled);

    // A table's dashes row: cells of dashes, each with an optional colon at either end, parted by pipes. It must hold
    // a pipe somewhere (TableStarts checks), or a lone "---" (a rule under a line) would make a one-column table.
    static readonly Regex TableDashes = new(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);

    // A pipe that parts two cells: any pipe not escaped by a backslash.
    static readonly Regex CellSplit = new(@"(?<!\\)\|", RegexOptions.Compiled);

    // The inline constructs, tried left to right, the first match winning at each place: code first, so nothing
    // inside it is read; then bold before italic, so ** is not read as two empty italics; then links, then a bare
    // address. A bare address stops before closing punctuation, so the full stop ending a sentence is not in the link.
    // Last, a double-quoted phrase (straight or curly quotes) on one line, up to 200 characters, starting and ending on
    // a non-space, holding none of the other constructs' marks (see the header): it is the copyable kind.
    static readonly Regex Inline = new(
        @"(?<code>`[^`\n]+`)" +
        @"|(?<bold>\*\*(?=\S)(?<boldText>.+?)(?<=\S)\*\*)" +
        @"|(?<italic>\*(?=[^\s*])(?<italicText>.+?)(?<=[^\s*])\*)" +
        @"|(?<link>\[(?<linkText>[^\]\n]+)\]\((?:<(?<linkUrl>[^>\n]+)>|(?<linkUrl>[^)\s]+))\))" +
        @"|(?<bare>(?:https?://|file:///)[^\s<>()\[\]]*[^\s<>()\[\].,;:!?'""])" +
        @"|(?<quote>(?<open>[""\u201C])(?![^""\u201D\n]*://)(?<quoteText>[^\s""\u201C\u201D`*\[\]](?:[^""\u201C\u201D\n`*\[\]]{0,198}[^\s""\u201C\u201D`*\[\]])?)(?<close>[""\u201D]))",
        RegexOptions.Compiled);

    /// <summary>
    /// The comment as a document, ready for a read-only RichTextBox. <paramref name="open"/> is what a click on a link
    /// does; left null, the default browser opens it, or a file link's plan is followed (see the header).
    /// </summary>
    /// <remarks><paramref name="copy"/> is where a click on code or a quote puts its text (see the header); left null,
    /// the clipboard. <paramref name="note"/> is where the note that follows a copy goes ("Copied", or why not); left
    /// null, a small tooltip at the mouse pointer (see "The note at the pointer" in the header).</remarks>
    public static FlowDocument Render(string markdown, Action<Uri>? open = null, Action<string>? copy = null,
        Action<string>? note = null)
    {
        // A copy handed in by a caller (the tests) always succeeds; the clipboard can be busy (CopyToClipboard).
        var put = copy is null ? (Func<string, bool>)CopyToClipboard : text => { copy(text); return true; };
        var tell = note is null ? (Action<TextElement, string>)ShowNote : (_, text) => note(text);
        var reading = new Reading(open ?? Open, new Copier(put, tell));
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = TextFont, TextAlignment = TextAlignment.Left };
        Blocks((markdown ?? "").Replace("\r\n", "\n").Split('\n'), reading).ToList().ForEach(document.Blocks.Add);
        return document;
    }

    /// <summary>
    /// One text's inline markdown alone (bold, italic, code, links, bare web addresses) as inlines for a TextBlock, with
    /// no block reading: a list marker or a heading's hashes stay the text they are, and a line break stays inside its
    /// run, so a TextBlock shows it exactly as it showed the plain text. For a task line's question and status line in
    /// the tasks window (task 08cdb052: "when asking for action, prs should have links, or artifacts; make that
    /// clickable"), where a session names the pull request to merge as [Deskbar #490](https://...) and the link has to
    /// be one click away. Links are web addresses and local files, as in a comment (see the header).
    /// </summary>
    public static IReadOnlyList<Inline> InlinesOf(string text, Action<Uri>? open = null) =>
        Inlines(text ?? "", new Reading(open ?? Open, null)).ToArray();

    // What a reading hands down to every construct: where a link's click goes, and where a copy goes. Copy is null
    // where nothing copies: in a TextBlock (InlinesOf) and inside a link's text (see the header).
    sealed record Reading(Action<Uri> Open, Copier? Copy)
    {
        public Reading WithoutCopy => this with { Copy = null };
    }

    // How a copyable part copies: Put places the text and says whether it got there (false: the clipboard was busy), and
    // Note tells the user which, beside the part that was pressed (ShowNote, or the tests' list).
    sealed record Copier(Func<string, bool> Put, Action<TextElement, string> Note);

    // --- blocks --------------------------------------------------------------------------------------------------

    // The lines as blocks, read top to bottom. Each step takes one block's lines off the front and passes the rest on,
    // so the reader is a fold over the lines without an index to keep in step.
    static IEnumerable<Block> Blocks(IReadOnlyList<string> lines, Reading open)
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
    static (Block? Block, int Taken) Next(IReadOnlyList<string> lines, Reading open)
    {
        var first = lines[0];
        if (string.IsNullOrWhiteSpace(first)) return (null, 1);
        if (first.TrimStart().StartsWith("```", StringComparison.Ordinal)) return Fenced(lines, open.Copy);
        if (Bullet.IsMatch(first)) return ListOf(lines, Bullet, TextMarkerStyle.Disc, open);
        if (Numbered.IsMatch(first)) return ListOf(lines, Numbered, TextMarkerStyle.Decimal, open);
        if (Heading.Match(first) is { Success: true } heading) return (HeadingOf(heading.Groups[1].Value, open), 1);
        if (TableStarts(lines, 0)) return TableOf(lines, open);

        // A paragraph runs to the next blank line, fence, list item, heading or table. A table may follow its
        // introducing line directly, as GitHub allows, so the lead-in line is not read as the table's header.
        var taken = lines.TakeWhile((line, i) => i == 0 || !(EndsParagraph(line) || TableStarts(lines, i))).ToArray();
        return (ParagraphOf(taken, open), taken.Length);
    }

    static bool EndsParagraph(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("```", StringComparison.Ordinal) || Bullet.IsMatch(line) || Numbered.IsMatch(line)
        || Heading.IsMatch(line);

    // Whether a table starts at line i: a line with a pipe, then a dashes row with as many cells (GitHub's rule; a
    // header of three cells over a dashes row of two is no table, and both stay the text they were).
    static bool TableStarts(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && lines[i + 1].Contains('|') && TableDashes.IsMatch(lines[i + 1])
        && CellsOf(lines[i]).Count == CellsOf(lines[i + 1]).Count;

    // A row's cells: the outer pipes dropped, parted at every unescaped pipe, each trimmed, "\|" turned into a pipe.
    static IReadOnlyList<string> CellsOf(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal)) row = row[..^1];
        return CellSplit.Split(row).Select(cell => cell.Trim().Replace("\\|", "|")).ToArray();
    }

    // A table from its header row, its dashes row and the body rows after them: every following line with a pipe in
    // it, up to a blank line, a line with no pipe or the start of another block (a heading, a list item or a fence,
    // even one with a pipe in it). Upstream markdown would take a pipeless line as one more row; here it ends the
    // table, since a session's sentence right under its table is far likelier than a one-cell row. A body row with
    // fewer cells than the header is filled with empty ones; extra cells are dropped, as upstream. Each column's
    // alignment comes from the colons on its dashes, and its share of the width from the longest text SHOWN in it (a
    // link counts its words, not its address), held between 4 and 40 characters' worth so a one-word column is not
    // crushed and a long sentence does not take the whole table.
    static (Block, int) TableOf(IReadOnlyList<string> lines, Reading open)
    {
        var header = CellsOf(lines[0]);
        var alignments = CellsOf(lines[1]).Select(AlignmentOf).ToArray();
        var body = lines.Skip(2).TakeWhile(line => line.Contains('|') && !EndsParagraph(line)).ToArray();
        var rows = new[] { header }
            .Concat(body.Select(line => CellsOf(line).Concat(Enumerable.Repeat("", header.Count)).Take(header.Count).ToArray()))
            .Select(cells => cells.Select((text, column) => CellOf(text, alignments[column], open)).ToArray())
            .ToArray();

        var table = new Table
        {
            Tag = TableTag,
            CellSpacing = 0,
            BorderBrush = TableRule,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 2, 0, 6),
        };
        Enumerable.Range(0, header.Count)
            .Select(column => rows.Max(row => ShownLength(row[column].Inlines)))
            .Select(longest => new TableColumn { Width = new GridLength(Math.Clamp(longest, 4, 40), GridUnitType.Star) })
            .ToList().ForEach(table.Columns.Add);

        // The header in semibold. Body cells set no weight of their own, so a document drawn semibold as a whole (a
        // question waiting on the user) keeps its table semibold too.
        rows[0].ToList().ForEach(cell => cell.FontWeight = FontWeights.SemiBold);
        var head = new TableRowGroup { Tag = TableHeaderTag, Background = TableHeaderFill };
        head.Rows.Add(RowOf(rows[0]));
        table.RowGroups.Add(head);
        var rest = new TableRowGroup();
        rows.Skip(1).ToList().ForEach(row => rest.Rows.Add(RowOf(row)));
        table.RowGroups.Add(rest);
        return (table, 2 + body.Length);
    }

    // One cell's text as a paragraph, read for inline markdown, aligned as its column.
    static Paragraph CellOf(string text, TextAlignment alignment, Reading open)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment };
        Inlines(text, open).ToList().ForEach(paragraph.Inlines.Add);
        return paragraph;
    }

    // How many characters the inlines show: runs counted, spans (bold, italic, links) walked into.
    static int ShownLength(IEnumerable<Inline> inlines) =>
        inlines.Sum(inline => inline switch
        {
            Run run => run.Text.Length,
            Span span => ShownLength(span.Inlines),
            _ => 0,
        });

    // One row of cells, each with the grid line on its right and bottom (the table draws the top and left ones, so no
    // line is doubled).
    static TableRow RowOf(IReadOnlyList<Paragraph> cells)
    {
        var row = new TableRow();
        cells.ToList().ForEach(paragraph => row.Cells.Add(new TableCell(paragraph)
        {
            BorderBrush = TableRule,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 2, 6, 2),
        }));
        return row;
    }

    // ":--" or "---" is left, "--:" right and ":-:" centre.
    static TextAlignment AlignmentOf(string dashes) =>
        (dashes.StartsWith(':'), dashes.EndsWith(':')) switch
        {
            (true, true) => TextAlignment.Center,
            (false, true) => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

    // A heading's one line, read for inline markdown like any other, in semibold with room above it to set it off
    // from the section before. No size of its own: it takes the document's, which the caller sets.
    static Paragraph HeadingOf(string text, Reading open)
    {
        var paragraph = new Paragraph { Tag = HeadingTag, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
        Inlines(text.TrimEnd(), open).ToList().ForEach(paragraph.Inlines.Add);
        return paragraph;
    }

    // Lines joined by line breaks, each read for inline markdown.
    static Paragraph ParagraphOf(IReadOnlyList<string> lines, Reading open)
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
    static (Block, int) Fenced(IReadOnlyList<string> lines, Copier? copy)
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
        // The whole block copies, its lines joined as Windows joins lines, so a script pastes as it was written.
        if (copy is not null && body.Length > 0) Copyable(code, string.Join(Environment.NewLine, body), copy);
        return (code, body.Length + (closed ? 2 : 1));
    }

    // Consecutive items of one kind of list. An indented line that is not an item continues the item above it, after
    // a line break, so a wrapped item stays one item.
    static (Block, int) ListOf(IReadOnlyList<string> lines, Regex item, TextMarkerStyle marker, Reading open)
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
    static IEnumerable<Inline> Inlines(string text, Reading open)
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

    static Inline Construct(Match match, Reading open)
    {
        if (match.Groups["code"].Success)
        {
            var code = new Run(match.Value[1..^1]) { Tag = CodeTag, FontFamily = CodeFont, Background = CodeFill };
            if (open.Copy is { } copy) Copyable(code, code.Text, copy);
            return code;
        }
        if (match.Groups["quote"].Success)
            return QuoteOf(match, open.Copy);
        if (match.Groups["bold"].Success)
            return new Bold().With(Inlines(match.Groups["boldText"].Value, open));
        if (match.Groups["italic"].Success)
            return new Italic().With(Inlines(match.Groups["italicText"].Value, open));
        if (match.Groups["link"].Success)
            return Address(match.Groups["linkUrl"].Value) is { } uri
                ? Link(Inlines(match.Groups["linkText"].Value, open.WithoutCopy), uri, open.Open)
                : new Run(match.Value);
        return Address(match.Value) is { } bare ? Link([new Run(match.Value)], bare, open.Open) : new Run(match.Value);
    }

    // A quoted phrase: its marks as plain text either side of its words, the words copyable on their own. Where nothing
    // copies (a TextBlock, a link's text) it is the text it was written as.
    static Inline QuoteOf(Match match, Copier? copy)
    {
        if (copy is null) return new Run(match.Value);
        var words = new Run(match.Groups["quoteText"].Value) { Tag = QuoteTag };
        Copyable(words, words.Text, copy);
        return new Span().With([new Run(match.Groups["open"].Value), words, new Run(match.Groups["close"].Value)]);
    }

    // Makes a run or a block copy `text` when pressed: the hand pointer and "Click to copy" on hover, the press taken
    // (so neither the editor's selection nor the bubble under it sees it), and a short flash of the copied part. The
    // flash puts back whatever background the part had at the press, so a restyle after Render (InboxMarks gives code
    // its own chip) survives it.
    static void Copyable(TextElement element, string text, Copier copy)
    {
        element.Cursor = System.Windows.Input.Cursors.Hand;
        element.ToolTip = CopyHint;
        element.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            // A double-click's second press is the same copy again: taken, so it selects nothing, but not repeated.
            if (e.ClickCount > 1) return;
            // The flash and "Copied" only when the text got there; a busy clipboard says so instead of pretending.
            var copied = copy.Put(text);
            if (copied) Flash(element);
            copy.Note(element, copied ? CopiedNote : NotCopiedNote);
        };
    }

    // THE NOTE AT THE POINTER (task f39f5db3, Petre: "when copying on a click, say that it's been copied in a small note
    // near the mouse pointer"). The green flash alone was easy to miss on a long command, and the hover text "Click to
    // copy" closes on the press, so nothing said the copy had happened. The note is a ToolTip opened by hand, placed
    // the way hover text is (PlacementMode.Mouse: just under the pointer), so it has the app's tooltip look with nothing
    // to style, and OwnToolTips' class handlers lift it above the bar like any other hover text (task d4695a97: an
    // owner-less popup slides under the bar's once-a-second climb otherwise).
    //   One note per UI thread, reused: a second copy moves it to the new press and starts its time again, so two notes
    //   never stand at once. It is closed and reopened rather than left open, because a popup that is already open does
    //   not follow the pointer to the new press.
    //   It goes after NoteTime by itself. WPF's ToolTipService leaves a tooltip it did not open alone, so a move of the
    //   pointer does not close it early; that is fine for a note this short.
    [ThreadStatic] static System.Windows.Controls.ToolTip? pointerNote;
    [ThreadStatic] static System.Windows.Threading.DispatcherTimer? noteTimer;

    /// <summary>This thread's note, for the tests; null until the first copy here.</summary>
    internal static System.Windows.Controls.ToolTip? NoteOnThisThread => pointerNote;

    static void ShowNote(TextElement element, string text)
    {
        var tip = pointerNote ??= new System.Windows.Controls.ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        var timer = noteTimer ??= NoteTimer(tip);
        tip.IsOpen = false;
        tip.Content = text;
        // The box the document sits in, so the popup opens on that window's screen and DPI. Null (a document not shown
        // anywhere) still opens at the pointer.
        tip.PlacementTarget = HostOf(element);
        tip.IsOpen = true;
        timer.Stop();
        timer.Start();
    }

    static System.Windows.Threading.DispatcherTimer NoteTimer(System.Windows.Controls.ToolTip tip)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = NoteTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tip.IsOpen = false;
            // Let go of the box it last stood by, so a task window closed after a copy is not kept alive by the note.
            tip.PlacementTarget = null;
        };
        return timer;
    }

    // The control a document element is shown in: up the logical tree (run, paragraph, FlowDocument, RichTextBox) to the
    // first UIElement.
    static UIElement? HostOf(DependencyObject element) =>
        LogicalTreeHelper.GetParent(element) switch
        {
            UIElement host => host,
            null => null,
            var parent => HostOf(parent),
        };

    // A press during a flash (a double-click's second press) only restarts the running flash: saving the background
    // then would save the green, and the flash would put the green back for good.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextElement, System.Windows.Threading.DispatcherTimer> Flashing = new();

    static void Flash(TextElement element)
    {
        if (Flashing.TryGetValue(element, out var running))
        {
            running.Stop();
            running.Start();
            return;
        }
        var before = element.ReadLocalValue(TextElement.BackgroundProperty);
        element.Background = CopiedFill;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = CopiedFlash };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Flashing.Remove(element);
            if (before == DependencyProperty.UnsetValue) element.ClearValue(TextElement.BackgroundProperty);
            else element.SetValue(TextElement.BackgroundProperty, before);
        };
        Flashing.Add(element, timer);
        timer.Start();
    }

    // The clipboard is shared with every program and one can hold it open for a moment; a copy that finds it busy is
    // traced and dropped rather than thrown on the UI thread (as TaskLinkClipboard does). Whether it got there decides
    // the note at the pointer.
    static bool CopyToClipboard(string text) =>
        Result.Try(() => System.Windows.Clipboard.SetText(text))
            .TapError(error => ClickTrace.Write($"copy from a comment: {error}"))
            .IsSuccess;

    // A hyperlink whose click goes to the opener. Click rather than RequestNavigate: there is no navigation host in a
    // RichTextBox, and Click is raised on a press in a read-only box with IsDocumentEnabled set.
    static Hyperlink Link(IEnumerable<Inline> content, Uri uri, Action<Uri> open)
    {
        // The hover text is what the click goes to: a file's path as Windows writes it, an address as it is.
        var link = new Hyperlink { NavigateUri = uri, ToolTip = FileLink.LocalPath(uri).GetValueOrDefault(uri.ToString()), Foreground = LinkInk }.With(content);
        link.Click += (_, e) =>
        {
            e.Handled = true;
            open(uri);
        };
        return link;
    }

    // Only absolute http and https addresses, and files on a drive of this machine, are links (see the header).
    static Uri? Address(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || FileLink.LocalPath(uri).HasValue) ? uri : null;

    static T With<T>(this T span, IEnumerable<Inline> inlines) where T : Span
    {
        inlines.ToList().ForEach(span.Inlines.Add);
        return span;
    }

    // The default opener: the shell hands a web address to the default browser, and a file link goes by its plan.
    // Internal for TasksBoard.OpenLink, which opens a task's link through it once it has gone to the task's workspace.
    internal static void Open(Uri uri) =>
        FileLink.LocalPath(uri).Match(OpenFile, () => Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, uri.ToString()));

    // A file link's click, by FileLink.Plan: a document opens in its own program; a folder opens in Explorer; any
    // other file is selected in Explorer and never started; a path that is gone opens the nearest folder above it.
    // Explorer gets the path in quotes, which a path cannot contain (FileLink.LocalPath refuses one that does).
    static void OpenFile(string path)
    {
        var (action, target) = FileLink.Plan(path, System.IO.File.Exists, System.IO.Directory.Exists);
        if (action == FileLinkAction.Nothing) ClickTrace.Write($"tasks window: nothing to open at {path}");
        else Start(action switch
        {
            FileLinkAction.Open => new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true },
            FileLinkAction.Reveal => new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{target}\""),
            _ => new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\""),
        }, target);
    }

    // A refusal (no browser registered, no program for the type) is traced, never thrown: a click on a comment must
    // not take the app down.
    static void Start(System.Diagnostics.ProcessStartInfo start, string what)
    {
        try
        {
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClickTrace.Write($"tasks window: could not open {what}: {e.Message}");
        }
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
