namespace TaskSpaces.Core.Domain;

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
    // One step up from the code's sizes: that step IS the "one more" that was asked for. Null in state.json
    // means this, so changing it here still reaches anyone who never touched the slider.
    public const double Default = 0.5;

    // Rails rather than preferences. Below -2 the smallest captions fall under 7.5 pt and stop being
    // readable on a scaled-down bar; above +4 the 58 DIP caption gutter wraps every workspace name.
    public const double Minimum = -2;
    public const double Maximum = 4;

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
