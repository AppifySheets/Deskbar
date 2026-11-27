using System.Windows;
using System.Windows.Documents;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The comment threads' markdown (MarkdownBlocks; docs/superpowers/specs/2026-11-22-tasks-board-design.md, "Comments" and
// "Left to the builder": no WebView, a subset of markdown rendered in WPF). One test per construct the subset covers,
// and one for what it does not: anything else shows as the text it is. FlowDocuments are dispatcher objects, so these
// run on an STA thread like the window tests, though nothing is shown.
public class MarkdownBlocksTests
{
    static string TextOf(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text;

    // A list item's own text, without the marker a TextRange over the whole item includes.
    static string ItemText(ListItem item) => string.Concat(item.Blocks.SelectMany(BlockInlines).OfType<Run>().Select(r => r.Text));

    static string TextOf(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;

    // Every inline in the document, however deep, in order.
    static IEnumerable<Inline> InlinesOf(FlowDocument document) =>
        document.Blocks.SelectMany(BlockInlines);

    static IEnumerable<Inline> BlockInlines(Block block) => block switch
    {
        Paragraph p => p.Inlines.SelectMany(Deep),
        List l => l.ListItems.SelectMany(i => i.Blocks).SelectMany(BlockInlines),
        Section s => s.Blocks.SelectMany(BlockInlines),
        Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks).SelectMany(BlockInlines),
        _ => [],
    };

    static IEnumerable<Inline> Deep(Inline inline) =>
        new[] { inline }.Concat(inline is Span span ? span.Inlines.SelectMany(Deep) : []);

    [Fact]
    public void Paragraphs_are_split_by_a_blank_line() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("The branch is built.\n\nSwapping in means stopping the build.");

        var paragraphs = document.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("The branch is built.", TextOf(paragraphs[0]));
        Assert.Equal("Swapping in means stopping the build.", TextOf(paragraphs[1]));
    });

    [Fact]
    public void A_single_newline_breaks_the_line_inside_a_paragraph() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("one\ntwo");

        var paragraph = Assert.Single(document.Blocks.OfType<Paragraph>());
        Assert.Contains(paragraph.Inlines, i => i is LineBreak);
    });

    [Fact]
    public void Double_stars_are_bold() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("this is **important** now");

        Assert.Equal("important", TextOf(Assert.Single(InlinesOf(document).OfType<Bold>())));
        Assert.Equal("this is important now", TextOf(document).Trim());
    });

    [Fact]
    public void Single_stars_are_italic() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("an *aside* here");

        Assert.Equal("aside", TextOf(Assert.Single(InlinesOf(document).OfType<Italic>())));
    });

    [Fact]
    public void Backticks_are_inline_code() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("run `deskbar.cmd help` first");

        var code = Assert.Single(InlinesOf(document).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag));
        Assert.Equal("deskbar.cmd help", code.Text);
        // Nothing inside code is read as markdown.
        Assert.Equal("a **b**", Assert.Single(InlinesOf(MarkdownBlocks.Render("`a **b**`")).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag)).Text);
    });

    [Fact]
    public void A_fenced_block_is_code_kept_line_for_line() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("Before\n\n```powershell\ndotnet build -c Release\n  **not bold**\n```\n\nAfter");

        var blocks = document.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(3, blocks.Count);
        var code = blocks[1];
        Assert.Equal(MarkdownBlocks.CodeBlockTag, code.Tag);
        Assert.Contains("dotnet build -c Release", TextOf(code));
        Assert.Contains("  **not bold**", TextOf(code));
        Assert.DoesNotContain(code.Inlines, i => i is Bold);
        Assert.DoesNotContain("powershell", TextOf(code));
    });

    [Fact]
    public void A_link_opens_through_the_opener() => StaThread.Run(() =>
    {
        var opened = new List<Uri>();
        var document = MarkdownBlocks.Render("see [the PR](https://github.com/AppifySheets/Deskbar/pull/398) now", opened.Add);

        var link = Assert.Single(InlinesOf(document).OfType<Hyperlink>());
        Assert.Equal("the PR", TextOf(link));
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal([new Uri("https://github.com/AppifySheets/Deskbar/pull/398")], opened);
    });

    [Fact]
    public void A_bare_web_address_is_a_link() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("CI: https://github.com/AppifySheets/Deskbar/actions/runs/1.");

        var link = Assert.Single(InlinesOf(document).OfType<Hyperlink>());
        // The full stop ending the sentence is not part of the address.
        Assert.Equal(new Uri("https://github.com/AppifySheets/Deskbar/actions/runs/1"), link.NavigateUri);
    });

    // A comment can come from a session, so a click must never run a program: a share (opening one signs in to its
    // server) and a scheme that starts a program stay text. What a local file's click does is FileLink's, in Core.
    [Theory]
    [InlineData("[setup](file://server/share/setup.html)")]
    [InlineData("[settings](ms-settings:display)")]
    [InlineData("[stream](file:///C:/docs/page.html:run.exe)")]
    public void A_link_that_is_neither_a_web_address_nor_a_local_file_stays_text(string markdown) => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render(markdown);

        Assert.Empty(InlinesOf(document).OfType<Hyperlink>());
        Assert.Equal(markdown, TextOf(document).Trim());
    });

    // Task f9f25172 ("when giving file links, they should be clickable too"): his screenshot's question named a mockup
    // page as a file link and the panel showed the markdown written out.
    [Theory]
    [InlineData("the page: [soft blue variants](file:///C:/Projects/design/light/index.html)", @"C:\Projects\design\light\index.html")]
    [InlineData(@"the page: [soft blue variants](C:\Projects\design\light\index.html)", @"C:\Projects\design\light\index.html")]
    [InlineData(@"the page: [soft blue variants](<C:\My Projects\light\index.html>)", @"C:\My Projects\light\index.html")]
    public void A_link_to_a_local_file_is_a_link(string markdown, string path) => StaThread.Run(() =>
    {
        var opened = new List<Uri>();
        var document = MarkdownBlocks.Render(markdown, opened.Add);

        var link = Assert.Single(InlinesOf(document).OfType<Hyperlink>());
        Assert.Equal("soft blue variants", TextOf(link));
        Assert.Equal(path, link.ToolTip);
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(path, Assert.Single(opened).LocalPath);
    });

    [Fact]
    public void A_bare_file_address_is_a_link() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("It is at file:///C:/Projects/design/index.html.");

        Assert.Equal(@"C:\Projects\design\index.html", Assert.Single(InlinesOf(document).OfType<Hyperlink>()).NavigateUri.LocalPath);
    });

    [Fact]
    public void Dashes_make_a_bullet_list() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("Left:\n- the hooks\n- **the skill**\n* the window");

        var list = Assert.Single(document.Blocks.OfType<List>());
        Assert.Equal(TextMarkerStyle.Disc, list.MarkerStyle);
        Assert.Equal(["the hooks", "the skill", "the window"], list.ListItems.Select(ItemText));
        Assert.Single(InlinesOf(document).OfType<Bold>());
    });

    [Fact]
    public void Numbers_make_a_numbered_list() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("3. build\n4. test");

        var list = Assert.Single(document.Blocks.OfType<List>());
        Assert.Equal(TextMarkerStyle.Decimal, list.MarkerStyle);
        Assert.Equal(3, list.StartIndex);
        Assert.Equal(["build", "test"], list.ListItems.Select(ItemText));
    });

    [Fact]
    public void Unknown_markdown_shows_as_text() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("#tag\n> quoted\n| a | b |\n**unclosed");

        Assert.All(document.Blocks, b => Assert.IsType<Paragraph>(b));
        var text = TextOf(document);
        // "#tag" has no space after its hash, so it is not a heading (as on GitHub) and stays as written.
        Assert.Contains("#tag", text);
        Assert.Contains("> quoted", text);
        Assert.Contains("| a | b |", text);
        Assert.Contains("**unclosed", text);
        Assert.Empty(InlinesOf(document).OfType<Bold>());
    });

    // Task descriptions are written in sections under "## ..." lines (2026-11-23): each heading is its own semibold
    // paragraph without its hashes, the closing kind of hashes dropped too, and it ends the paragraph above it.
    [Fact]
    public void Headings_are_semibold_lines_without_their_hashes() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("intro line\n## What you need to do next\n1. answer\n### Done ###");

        var headings = document.Blocks.OfType<Paragraph>().Where(p => Equals(p.Tag, MarkdownBlocks.HeadingTag)).ToList();
        Assert.Equal(["What you need to do next", "Done"], headings.Select(h => TextOf(h).Trim()));
        Assert.All(headings, h => Assert.Equal(FontWeights.SemiBold, h.FontWeight));
        Assert.Equal("intro line", TextOf(document.Blocks.First()).Trim());
        Assert.Single(document.Blocks.OfType<List>());
    });

    // A table as a session writes one (task 24813c78, where the Inbox chat showed it as rows of pipes): a header row, a
    // dashes row, body rows. The cells hold their text without the pipes, the header is its own tagged row group in
    // semibold, colons align columns, and bold inside a cell is still bold.
    static IReadOnlyList<string> CellTexts(TableRow row) => row.Cells.Select(c => TextOf(c).Trim()).ToArray();

    [Fact]
    public void A_pipe_table_is_a_table() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render(
            "Where the time goes:\n\n| Counterparties | Window | Sale lines |\n|---|:---:|---:|\n| one customer | 2 years | 37k |\n| all | 1 year | **over 10M** |\n\nafter");

        var table = Assert.Single(document.Blocks.OfType<Table>());
        Assert.Equal(MarkdownBlocks.TableTag, table.Tag);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(MarkdownBlocks.TableHeaderTag, table.RowGroups[0].Tag);
        var header = Assert.Single(table.RowGroups[0].Rows);
        Assert.Equal(["Counterparties", "Window", "Sale lines"], CellTexts(header));
        Assert.All(header.Cells, c => Assert.Equal(FontWeights.SemiBold, ((Paragraph)c.Blocks.FirstBlock).FontWeight));
        var body = table.RowGroups[1].Rows;
        Assert.Equal(["one customer", "2 years", "37k"], CellTexts(body[0]));
        Assert.Equal(["all", "1 year", "over 10M"], CellTexts(body[1]));
        Assert.Equal([TextAlignment.Left, TextAlignment.Center, TextAlignment.Right],
            body[0].Cells.Select(c => ((Paragraph)c.Blocks.FirstBlock).TextAlignment));
        Assert.Equal("over 10M", TextOf(Assert.Single(InlinesOf(document).OfType<Bold>())));
        Assert.Equal(["Where the time goes:", "after"], document.Blocks.OfType<Paragraph>().Select(p => TextOf(p).Trim()));
    });

    // As upstream markdown has it: a table may follow its lead-in line with no blank line between, rows may go without
    // outer pipes, a short row is filled with empty cells, and an escaped pipe stays inside its cell. Unlike upstream,
    // a line with no pipe ends the table (it is far likelier a sentence than a one-cell row; see TableOf).
    [Fact]
    public void A_table_may_follow_its_lead_in_line_and_rows_may_be_loose() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("lead in\na | b\n--- | ---\nx \\| y | z\nshort |\nafter");

        var table = Assert.Single(document.Blocks.OfType<Table>());
        Assert.Equal(["a", "b"], CellTexts(table.RowGroups[0].Rows[0]));
        Assert.Equal(["x | y", "z"], CellTexts(table.RowGroups[1].Rows[0]));
        Assert.Equal(["short", ""], CellTexts(table.RowGroups[1].Rows[1]));
        Assert.Equal(["lead in", "after"], document.Blocks.OfType<Paragraph>().Select(p => TextOf(p).Trim()));
    });

    // A heading or list item right under a table starts its own block even with a pipe in it; body cells carry no
    // weight of their own, so a document drawn semibold keeps them semibold; and a link's address does not count
    // toward its column's share of the width, only its words.
    [Fact]
    public void A_table_ends_at_the_next_block_and_sizes_columns_by_shown_text() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render(
            "| PR | What |\n|---|---|\n| [#306](https://example.com/a/very/long/address/that/goes/on/and/on/for/ever) | a much longer summary of what it does |\n## Next | step\n- a | b");

        var table = Assert.Single(document.Blocks.OfType<Table>());
        Assert.Single(table.RowGroups[1].Rows);
        Assert.Single(document.Blocks.OfType<List>());
        Assert.Single(document.Blocks.OfType<Paragraph>(), p => Equals(p.Tag, MarkdownBlocks.HeadingTag));
        Assert.True(table.Columns[0].Width.Value < table.Columns[1].Width.Value);
        Assert.All(table.RowGroups[1].Rows[0].Cells,
            c => Assert.Equal(DependencyProperty.UnsetValue, c.Blocks.FirstBlock.ReadLocalValue(TextElement.FontWeightProperty)));
    });

    [Fact]
    public void A_table_whose_dashes_row_has_another_cell_count_stays_text() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("| a | b | c |\n|---|---|\n| 1 | 2 | 3 |");

        Assert.Empty(document.Blocks.OfType<Table>());
        Assert.Contains("| a | b | c |", TextOf(document));
    });

    [Fact]
    public void Nothing_renders_as_an_empty_document() => StaThread.Run(() =>
        Assert.Empty(MarkdownBlocks.Render("   \n\n").Blocks));

    // --- copy on a click (task b6af9445) ---------------------------------------------------------------------------
    // The copies go to a list handed in, never to the clipboard, which is shared with the person at the machine.

    // A left press on the element, as the read-only box routes one to the run under the pointer; true when taken.
    static bool Press(TextElement element)
    {
        var press = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        };
        element.RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public void Inline_code_copies_the_whole_command_when_pressed() => StaThread.Run(() =>
    {
        var copied = new List<string>();
        var document = MarkdownBlocks.Render("on contabo1 run `docker logs contabo1-infisical-backup-infisical-backup-1` around it", copy: copied.Add);

        var code = Assert.Single(InlinesOf(document).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag));
        Assert.Equal(MarkdownBlocks.CopyHint, code.ToolTip);
        Assert.True(Press(code));
        Assert.Equal(["docker logs contabo1-infisical-backup-infisical-backup-1"], copied);
    });

    [Fact]
    public void A_fenced_block_copies_all_its_lines() => StaThread.Run(() =>
    {
        var copied = new List<string>();
        var document = MarkdownBlocks.Render("```\ngit pull --rebase\n  ./bootstrap.sh\n```", copy: copied.Add);

        var block = Assert.Single(document.Blocks.OfType<Paragraph>());
        Assert.True(Press(block));
        Assert.Equal([$"git pull --rebase{Environment.NewLine}  ./bootstrap.sh"], copied);
    });

    [Fact]
    public void Quoted_words_copy_without_their_quote_marks() => StaThread.Run(() =>
    {
        var copied = new List<string>();
        var document = MarkdownBlocks.Render("then \"Pull and redeploy\" and “merge it”", copy: copied.Add);

        var quotes = InlinesOf(document).OfType<Run>().Where(r => Equals(r.Tag, MarkdownBlocks.QuoteTag)).ToList();
        Assert.Equal(["Pull and redeploy", "merge it"], quotes.Select(q => q.Text));
        // The marks stay on screen, as written.
        Assert.Equal("then \"Pull and redeploy\" and “merge it”", TextOf(document).Trim());
        quotes.ForEach(q => Press(q));
        Assert.Equal(["Pull and redeploy", "merge it"], copied);
    });

    [Fact]
    public void A_quote_holding_other_markdown_keeps_it() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("say \"[PR 22](https://example.com/22)\" or \"https://example.com\" or \"`x`\"", copy: _ => { });

        Assert.DoesNotContain(InlinesOf(document).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.QuoteTag));
        Assert.Equal(2, InlinesOf(document).OfType<Hyperlink>().Count());
        Assert.Single(InlinesOf(document).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag));
    });

    [Fact]
    public void Code_inside_a_link_and_a_task_line_copy_nothing() => StaThread.Run(() =>
    {
        var copied = new List<string>();
        var document = MarkdownBlocks.Render("[`deskbar.cmd`](https://example.com)", copy: copied.Add);
        var inLink = Assert.Single(InlinesOf(document).OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag));
        Assert.False(Press(inLink));

        var line = MarkdownBlocks.InlinesOf("run `deskbar.cmd help` and \"Done\"").SelectMany(Deep).ToList();
        var code = Assert.Single(line.OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.CodeTag));
        Assert.Null(code.ToolTip);
        Assert.DoesNotContain(line.OfType<Run>(), r => Equals(r.Tag, MarkdownBlocks.QuoteTag));
        Assert.Empty(copied);
    });
}
