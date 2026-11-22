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

    // A comment can come from a session, so a click must never run a program: only web links are links.
    [Fact]
    public void A_link_that_is_not_a_web_address_stays_text() => StaThread.Run(() =>
    {
        var document = MarkdownBlocks.Render("[setup](file:///C:/Windows/System32/cmd.exe)");

        Assert.Empty(InlinesOf(document).OfType<Hyperlink>());
        Assert.Equal("[setup](file:///C:/Windows/System32/cmd.exe)", TextOf(document).Trim());
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
        var document = MarkdownBlocks.Render("# Heading\n> quoted\n| a | b |\n**unclosed");

        Assert.All(document.Blocks, b => Assert.IsType<Paragraph>(b));
        var text = TextOf(document);
        Assert.Contains("# Heading", text);
        Assert.Contains("> quoted", text);
        Assert.Contains("| a | b |", text);
        Assert.Contains("**unclosed", text);
        Assert.Empty(InlinesOf(document).OfType<Bold>());
    });

    [Fact]
    public void Nothing_renders_as_an_empty_document() => StaThread.Run(() =>
        Assert.Empty(MarkdownBlocks.Render("   \n\n").Blocks));
}
