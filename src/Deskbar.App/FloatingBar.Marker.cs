using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Deskbar.App;

// THE WINDOW MARKERS ON AN ICON. Petre: "i want active windows / icons to be highlighted better", then "c" from
// the mockup, then "should apply to collapsed bars as well", and on seeing it live, "also give collapsed icons that
// kind of halo look".
//
// Two markers, one shape, two weights, because they say the same thing in two tenses:
//
//   focused   "you are in this window"      solid white ring, 18% fill, a glow at 70%, a white bar on the left
//   landing   "you will be in this window"  the same ring and bar at 70% white, a glow at 40%, no fill
//
// The landing marker sits on the window a workspace brings forward when you switch to it (WindowRow.WillActivate),
// so it is the one the collapsed rows of the OTHER workspaces carry. It was a 22% ring before, and the focused one
// a 60% ring on a 13% fill: two faint grey boxes nobody could tell apart at a glance, which was the complaint. The
// landing glow is the fainter one, and it has no fill, so "you are here" still wins over "you will be there": with
// both at full strength (the mockup's other column) every row would look focused.
//
// The bar on the left is the cue nothing else on the bar uses. The ring alone is a thin line, and a thin white
// line is exactly what gets lost on light artwork; the bar is three pixels wide and stands off the icon's edge,
// so it reads on any icon.
//
// NEITHER ADDITION COSTS LAYOUT, the rule every marker on this bar follows (the bar is SizeToContent, so a row that
// grows a pixel moves the window). The glow is a blurred copy of the ring drawn behind the button, and the bar
// hangs off the left edge on a negative margin, like the pin badge (WithPinBadge): both measure as nothing, so a
// marker moving from one icon to another cannot nudge anything. The bar covers only the neighbour's 1px padding
// and border and this icon's own border, never artwork: icons sit edge to edge (IconButton's margin), and each
// keeps 2px between its edge and its art.
public partial class FloatingBar
{
    // The marker's two extra parts, as attached properties the icon template binds to. Properties rather than
    // reaching into the template from code, because Titled swaps an icon's template for the stretching one AFTER
    // IconButton has painted it, and a part set by hand on the first template would be lost with it; a property
    // lives on the button and both templates read it.
    //
    // Public because the template is parsed from markup, and XAML only resolves public members.
    public static readonly DependencyProperty MarkerBarProperty =
        DependencyProperty.RegisterAttached("MarkerBar", typeof(Brush), typeof(FloatingBar), new PropertyMetadata(null));

    // The glow's strength, as the halo's opacity: 0 is no glow at all.
    public static readonly DependencyProperty MarkerGlowProperty =
        DependencyProperty.RegisterAttached("MarkerGlow", typeof(double), typeof(FloatingBar), new PropertyMetadata(0.0));

    // The glow's colour: white for the focused and landing windows, the pulsing orange for a window whose session
    // asked for the user (FloatingBar.Asking.cs).
    public static readonly DependencyProperty MarkerGlowBrushProperty =
        DependencyProperty.RegisterAttached("MarkerGlowBrush", typeof(Brush), typeof(FloatingBar), new PropertyMetadata(Brushes.White));

    public static Brush GetMarkerGlowBrush(DependencyObject element) => (Brush)element.GetValue(MarkerGlowBrushProperty);
    public static void SetMarkerGlowBrush(DependencyObject element, Brush value) => element.SetValue(MarkerGlowBrushProperty, value);

    public static Brush? GetMarkerBar(DependencyObject element) => (Brush?)element.GetValue(MarkerBarProperty);
    public static void SetMarkerBar(DependencyObject element, Brush? value) => element.SetValue(MarkerBarProperty, value);
    public static double GetMarkerGlow(DependencyObject element) => (double)element.GetValue(MarkerGlowProperty);
    public static void SetMarkerGlow(DependencyObject element, double value) => element.SetValue(MarkerGlowProperty, value);

    static readonly Brush FocusedRing = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    static readonly Brush FocusedFill = Frozen(0x2E, 0xFF, 0xFF, 0xFF);
    static readonly Brush FocusedBar = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    const double FocusedGlow = 0.7;
    static readonly Brush LandingRing = Frozen(0xB3, 0xFF, 0xFF, 0xFF);
    static readonly Brush LandingBar = Frozen(0xB3, 0xFF, 0xFF, 0xFF);
    const double LandingGlow = 0.4;

    // Paints one icon from the window's facts, the one place that decides how a marker looks: IconButton calls it
    // when the icon is built and ApplyCandidate whenever the amber ring moves, so the two cannot drift apart.
    //
    // `ringed` is the amber candidate ring (see ApplyCandidate): it takes the RING, because "this is what a click or
    // the chord would bring forward" is the more immediate claim while it lasts, and keeps the fill it always had.
    // The bar and the glow stay with the window's own facts underneath, so hovering the focused window does not
    // stop it looking focused. Focused wins over landing where both could apply, as it always has: an icon is only
    // both if the suppression in OverviewBuilder.WillActivate ever lapses.
    //
    // `asking` is the pulsing orange of a window whose session asked for the user (FloatingBar.Asking.cs), or null.
    // It takes the ring and a full-strength glow, below only the amber ring: a hover is the pointer, there now.
    internal static void PaintMarker(Button icon, bool ringed, bool focused, bool landing, Brush? asking = null)
    {
        icon.BorderBrush = ringed ? CandidateRowRing
            : asking ?? (focused ? FocusedRing
                : landing ? LandingRing
                : Brushes.Transparent);
        icon.Background = ringed || focused ? FocusedFill : Brushes.Transparent;
        SetMarkerBar(icon, focused ? FocusedBar : landing ? LandingBar : null);
        SetMarkerGlow(icon, asking is not null ? 1.0 : focused ? FocusedGlow : landing ? LandingGlow : 0);
        SetMarkerGlowBrush(icon, asking ?? Brushes.White);
    }

    // BareButton and BareStretchButton (see there for why the stock chrome is gone) plus the marker's two parts:
    // the glow behind the border, collapsed while MarkerGlow is 0 so that the hundred-odd unmarked icons carry
    // no effect to render, and the bar in front of it, whose brush is null, and so draws nothing, on an unmarked
    // icon. The one trigger is ours, not a theme's: BareButton's "no triggers" was about the stock hover layers.
    internal static readonly ControlTemplate MarkedButton = MarkedTemplate("Center");
    static readonly ControlTemplate MarkedStretchButton = MarkedTemplate("Stretch");

    static ControlTemplate MarkedTemplate(string contentAlignment)
    {
        // The app prefix must name this assembly, which the tests load under the same name.
        var context = new ParserContext();
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("app", $"clr-namespace:Deskbar.App;assembly={typeof(FloatingBar).Assembly.GetName().Name}");

        // The glow is a 2px white ring one pixel outside the border, blurred, at MarkerGlow's opacity; at Radius 6
        // and 70% it reads as a halo of about four pixels without fogging the neighbours. The bar is 3px wide starting 2px left of the
        // edge (see the header for what it covers) and 4px short of the top and bottom, so it reads as a bar
        // rather than as a thicker side of the ring.
        return Sealed((ControlTemplate)XamlReader.Parse(
            $$$"""
            <ControlTemplate TargetType="Button">
              <Grid>
                <Border x:Name="Glow" BorderThickness="2" Margin="-1" IsHitTestVisible="False"
                        BorderBrush="{Binding Path=(app:FloatingBar.MarkerGlowBrush), RelativeSource={RelativeSource TemplatedParent}}"
                        Opacity="{Binding Path=(app:FloatingBar.MarkerGlow), RelativeSource={RelativeSource TemplatedParent}}">
                  <Border.Effect>
                    <BlurEffect Radius="6" />
                  </Border.Effect>
                </Border>
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="{{{contentAlignment}}}" VerticalAlignment="Center" />
                </Border>
                <Rectangle Width="3" HorizontalAlignment="Left" Margin="-2,4,0,4" RadiusX="1" RadiusY="1"
                           IsHitTestVisible="False"
                           Fill="{Binding Path=(app:FloatingBar.MarkerBar), RelativeSource={RelativeSource TemplatedParent}}" />
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="app:FloatingBar.MarkerGlow" Value="0">
                  <Setter TargetName="Glow" Property="Visibility" Value="Collapsed" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """,
            context));
    }
}
