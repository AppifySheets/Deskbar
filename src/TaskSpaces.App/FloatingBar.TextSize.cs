using System.Windows.Controls;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.App;

// THE BAR'S TEXT SIZE, one setting for all of it. Petre: "increase font size by one more and make it
// configurable". The sizes written at each piece of text (captions 9.5, row labels 10.5, ...) are the BASE;
// Text() adds the offset from Manage's Settings tab (BarText, default +4), so every piece moves together and
// they keep their proportions.
//
// Deliberately NOT scaled: text that sits inside a fixed-size chip, because growing it would break the chip
// or the row height the icon cell guarantees. The tab count on an icon, the placeholder letter in its 20 DIP
// square, the volume percent inside its 32 DIP dial, and the glyph buttons.
//
// Nor THE CLOCK. Its 14.5 over 13 was tuned on its own, in rounds, to match the taskbar's clock, and it rode
// the offset only briefly: at the +4 default Petre's verdict was "watch and date are too big". It answers to
// the taskbar it imitates, not to the bar's captions, so it keeps its own sizes (FloatingBar.Shell.cs).
//
// Live: rows are rebuilt from scratch on the state pulse a change raises, so their text picks the new size up
// by itself. The now-playing line and the drop readout are built ONCE and kept, so ApplyTextSize sets theirs
// again on every rebuild.
public partial class FloatingBar
{
    double TextOffset => BarText.Clamp(manager.State.BarTextOffset);

    // A bar text's size from its base size.
    double Text(double baseSize) => BarText.Size(baseSize, TextOffset);

    // The workspace caption's size, which the group header and the caption line heights are measured from.
    double GroupHeaderFontSize => Text(9.5);

    // The text that outlives a rebuild.
    void ApplyTextSize()
    {
        Sized(trackTitle, 11);
        Sized(trackDetail, 10);
        Sized(readoutText, 11.5);
        ApplyStatsTextSize();
    }

    void Sized(TextBlock? text, double baseSize, double? lineHeight = null)
    {
        if (text is null) return;
        text.FontSize = Text(baseSize);
        if (lineHeight is { } line) text.LineHeight = Text(line);
    }
}
