namespace Deskbar.Core.Domain;

// How big the bar draws its ICONS, as a factor over the sizes written in the code: the app icons in the rows, the
// pinned apps with Start and search beside them, and the bottom strip's icons. Petre: "let's also make icon sizes
// configurable", "as a separate setting", "including icons at the bottom". Separate from BarScale, which scales
// the whole bar text and all, and from the text size setting, which leaves the icons alone: this one moves only
// the pictures, so the clock, the captions and the window titles keep their size.
//
// Same nullable, no-migration pattern as the other bar settings: null is "never configured" and means 100%.
public static class IconScaling
{
    public const double Default = 1.0;

    // 60% to 160% in steps of 5. Below 60 a 20 DIP icon is 12 DIP and its overlays (the tab count, the
    // attention dot) cover most of it; above 160 a row of three icons outgrows the narrowest bar the window
    // allows. The rails also stop a hand-edited state.json from producing an unusable bar.
    public const double Minimum = 0.6;
    public const double Maximum = 1.6;
    public const double Step = 0.05;

    // NaN and infinity are the default rather than a clamp, as in BarScaling, because either would make WPF's
    // layout throw somewhere far from here. Snapped to the step so a hand-edited 1.03 lands on a size the
    // slider can show.
    public static double Clamp(double? value) =>
        value is not { } scale || double.IsNaN(scale) || double.IsInfinity(scale)
            ? Default
            : Math.Round(Math.Clamp(scale, Minimum, Maximum) / Step) * Step;
}
