namespace Deskbar.Core.Domain;

// The bar's text size, as ONE setting that moves every piece of bar text together.
//
// Petre, twice: "let's increase font sizes by the smallest amount" (every bar text up half a point together),
// then "increase font size by one more and make it configurable". The sizes written in the bar's code are the
// BASE; this offset is added to each of them, so captions, the clock, the now-playing line and the titled list
// keep their proportions to one another however far the setting moves. A multiplier was the alternative and
// was not taken: "one more" was asked in the same half-point steps the last bump used, and a multiplier would
// move a 14.5 pt clock and a 9 pt caption by different amounts for one notch of the slider.
//
// Pure and in Core, like BarFading, so the clamp is testable without constructing a window.
public static class BarText
{
    // Four points up from the code's sizes. It was half a point (the "one more" first asked for) until Petre
    // tried the slider's top: "i set text size to max and it's much better. make this, max size the default,
    // ability to up or down". So the old maximum is the default now. Null in state.json means this, so
    // changing it here still reaches anyone who never touched the slider.
    public const double Default = 4;

    // Rails rather than preferences, with room either side of the default. Below -2 the smallest captions
    // fall under 7.5 pt and stop being readable on a scaled-down bar. The old ceiling of +4 was set for the
    // 58 DIP caption gutter, which wrapped every name past it; captions have had a line of their own since,
    // and trim rather than wrap. The clock does not follow this setting at all (see FloatingBar.TextSize.cs),
    // so +8 only has to keep the captions and the now-playing line sane.
    public const double Minimum = -2;
    public const double Maximum = 8;

    // Every size on the bar moves in half points, so the offset does too.
    public const double Step = 0.5;

    // Null means never configured; NaN and infinity are rejected because either would reach WPF's
    // layout as a font size and throw far from here. Snapped to the step, then held inside the rails.
    public static double Clamp(double? value) =>
        value is not { } offset || double.IsNaN(offset) || double.IsInfinity(offset)
            ? Default
            : Math.Clamp(Math.Round(offset / Step, MidpointRounding.AwayFromZero) * Step, Minimum, Maximum);

    // A piece of bar text's size: its base from the code, plus the offset.
    public static double Size(double baseSize, double offset) => baseSize + offset;
}
