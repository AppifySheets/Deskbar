using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Deskbar.App;

// A TASK TITLE'S TWO ROWS ON THE BAR (task acd76fbe, Petre: "remove the checkbox and utilize the second row fully").
// A bar line is at most two rows of text, and the workspace chip sits at the right end of the first row. A plain
// TextBlock wraps every line at one width, so the second row used to stop where the chip starts, leaving an empty
// strip under the chip (the screenshot on the task). WPF's TextBlock cannot wrap its lines at different widths, so
// TaskLineFace gives the title the whole line's width and this class ends the first row early, with a LineBreak placed
// where the words stop fitting beside the chip. The second row then runs on to the line's right edge.
//
// Why a LineBreak in the one TextBlock and not two TextBlocks (one per row): the title stays one element with all of
// its runs, so its tooltip, its strike-through while closing, its ellipsis at the end of the second row (MaxHeight cuts
// at two rows and TextTrimming ends the last one) and everything that reads the runs (the bar's tests, automation)
// stay as they were. A Run cut by the break is split into two Runs with the same look, the space staying at the end of
// the first one, so the runs joined still read exactly as the title and detail did.
//
// Where the first row ends: the last word break (after a space) whose words fit the room beside the chip, measured
// with FormattedText at the title's own font, run by run (bold and italic titles are wider). A first word longer than
// the room breaks inside the word at the last character that fits, as TextBlock's own wrapping would. Text that fits
// the room whole gets no break and stays one row. An inline element (the Done tick) counts at its measured width.
internal static class TitleRows
{
    // The character standing in for an inline element while measuring (it is never drawn).
    const char ObjectMark = '￼';

    // One run of the title as measured: its text and the look that changes its width, or an element's width.
    sealed record Piece(string Text, FontWeight Weight, FontStyle Style, double ObjectWidth);

    /// <summary>
    /// Where the first row of <paramref name="source"/> ends so that it fits <paramref name="room"/> DIP, as a character
    /// offset into the runs' joined text (an inline element counts as one character), or None when it all fits in the
    /// room and needs no break.
    /// </summary>
    internal static int? BreakAt(TextBlock text, IReadOnlyList<Inline> source, double room)
    {
        var pieces = source.Select(PieceOf).OfType<Piece>().ToList();
        var whole = string.Concat(pieces.Select(p => p.Text));
        double Width(int length) => WidthOf(text, pieces, length);
        if (whole.Length == 0 || Width(whole.Length) <= room) return null;
        // Word breaks: just after each space, as TextBlock wraps. The longest prefix that fits wins.
        var words = Enumerable.Range(1, Math.Max(0, whole.Length - 1)).Where(i => whole[i - 1] == ' ' && whole[i] != ' ').ToList();
        var fits = LastFitting(words, i => Width(i) <= room);
        if (fits > 0) return fits;
        // The first word alone is wider than the room: break inside it, at the last character that fits (at least one
        // character, so the first row is never empty while there is any room at all).
        var chars = LastFitting(Enumerable.Range(1, Math.Max(0, (words.Count > 0 ? words[0] : whole.Length) - 1)).ToList(), i => Width(i) <= room);
        return Math.Max(chars, room > 0 ? 1 : 0);
    }

    // The last of the ascending offsets that fits, or 0 when none does. A binary search: a longer prefix is never
    // narrower (trailing spaces are left out of the width), and a long question has dozens of word breaks, measured on
    // every layout of every line.
    static int LastFitting(IReadOnlyList<int> offsets, Func<int, bool> fits)
    {
        var (low, high, found) = (0, offsets.Count - 1, 0);
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (fits(offsets[middle]))
            {
                found = offsets[middle];
                low = middle + 1;
            }
            else
                high = middle - 1;
        }
        return found;
    }

    /// <summary>
    /// The title's runs with a LineBreak at <paramref name="at"/> (see BreakAt), or the runs as they are when None. A Run
    /// the break falls inside is split into two Runs with the same look; every other inline is the original object.
    /// </summary>
    internal static IReadOnlyList<Inline> WithBreak(IReadOnlyList<Inline> source, int? at)
    {
        if (at is not { } offset) return source;
        var start = 0;
        return source.SelectMany(inline =>
        {
            var length = LengthOf(inline);
            var from = start;
            start += length;
            if (offset == from) return new Inline[] { new LineBreak(), inline };
            if (inline is Run run && offset > from && offset < from + length)
                return new Inline[] { CopyOf(run, run.Text[..(offset - from)]), new LineBreak(), CopyOf(run, run.Text[(offset - from)..]) };
            return new[] { inline };
        }).ToList();
    }

    // A run's text in a new Run with the same look (the parts the title sets: ink, weight, slant, baseline).
    static Run CopyOf(Run run, string text) => new(text)
    {
        Foreground = run.Foreground,
        FontWeight = run.FontWeight,
        FontStyle = run.FontStyle,
        BaselineAlignment = run.BaselineAlignment,
    };

    static int LengthOf(Inline inline) => inline switch
    {
        Run run => run.Text.Length,
        InlineUIContainer => 1,
        _ => 0,
    };

    static Piece? PieceOf(Inline inline) => inline switch
    {
        Run run => new Piece(run.Text, run.FontWeight, run.FontStyle, 0),
        InlineUIContainer { Child: { } child } => new Piece(ObjectMark.ToString(), FontWeights.Normal, FontStyles.Normal, ElementWidth(child)),
        _ => null,
    };

    static double ElementWidth(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    // The width of the first `length` characters of the pieces, trailing spaces left out (they hang past a row's end).
    static double WidthOf(TextBlock text, IReadOnlyList<Piece> pieces, int length)
    {
        var start = 0;
        var cut = pieces.Select(p =>
        {
            var take = Math.Clamp(length - start, 0, p.Text.Length);
            start += p.Text.Length;
            return p with { Text = p.Text[..take] };
        }).Where(p => p.Text.Length > 0).ToList();
        var objects = cut.Where(p => p.ObjectWidth > 0).Sum(p => p.ObjectWidth);
        var runs = cut.Where(p => p.ObjectWidth == 0).ToList();
        var joined = string.Concat(runs.Select(p => p.Text)).TrimEnd(' ');
        if (joined.Length == 0) return objects;
        var formatted = new FormattedText(joined, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        // Each run's own weight and slant over its characters (the title is bold when important, italic when urgent).
        var at = 0;
        runs.ForEach(p =>
        {
            var span = Math.Min(p.Text.Length, joined.Length - at);
            if (span > 0)
            {
                formatted.SetFontWeight(p.Weight, at, span);
                formatted.SetFontStyle(p.Style, at, span);
            }
            at += p.Text.Length;
        });
        return formatted.WidthIncludingTrailingWhitespace + objects;
    }
}
