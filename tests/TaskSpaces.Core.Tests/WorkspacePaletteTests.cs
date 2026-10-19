using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// The vivid palette (2.1.1). Petre: "now that we have just a line for the colors, make it more colorful and
// vivid and youthful", then "vivid" from the mockup. These pin the three things that are easy to lose: the
// order that keeps neighbouring rows apart, the retired muted colours following the palette, and the old
// names still working.
public class WorkspacePaletteTests
{
    // OKLab from sRGB, the space the order was solved in: distance there matches what the eye sees.
    static (double L, double A, double B) OkLab(string hex)
    {
        static double Lin(int c) => c / 255.0 is var v && v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        var (r, g, b) = (Lin(Convert.ToInt32(hex[1..3], 16)), Lin(Convert.ToInt32(hex[3..5], 16)), Lin(Convert.ToInt32(hex[5..7], 16)));
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static double Distance(string x, string y)
    {
        var (a, b) = (OkLab(x), OkLab(y));
        return Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2));
    }

    // Around the ring, since a tenth workspace wears slot 0 again directly under the ninth. 0.327 was the best
    // order found with Indigo first; the mockup's own order managed 0.221.
    [Fact]
    public void Neighbouring_colours_stay_far_apart()
    {
        var hexes = WorkspacePalette.Swatches.Select(s => s.Hex).ToList();
        var worst = hexes.Select((hex, i) => Distance(hex, hexes[(i + 1) % hexes.Count])).Min();
        Assert.True(worst >= 0.32, $"the closest neighbouring pair is {worst:F3} apart");
    }

    [Fact]
    public void Indigo_stays_first_to_match_the_app_icon() =>
        Assert.Equal("Indigo", WorkspacePalette.Swatches[0].Name);

    // A colour picked from the old muted palette is drawn as the vivid swatch it became.
    [Theory]
    [InlineData("#3C48BE", "#5B6CFF")] // indigo
    [InlineData("#2e7d6b", "#19D3B5")] // teal, lower case as a hand-edited file might have it
    [InlineData("#A8562C", "#FF5A5F")] // rust
    public void A_retired_colour_is_drawn_as_its_vivid_replacement(string stored, string drawn) =>
        Assert.Equal(drawn, WorkspacePalette.For(stored, 4));

    // A Custom… colour, and the transparent opt-out, are left as chosen.
    [Theory]
    [InlineData("#123456")]
    [InlineData(WorkspacePalette.None)]
    public void Anything_else_is_left_alone(string stored) =>
        Assert.Equal(stored, WorkspacePalette.Current(stored));

    [Theory]
    [InlineData("sky", "#2FB4FF")]
    [InlineData("Hot Pink", "#FF4D9D")]
    [InlineData("steel", "#2FB4FF")]  // retired names stay accepted
    [InlineData("amber", "#FF8A2B")]
    public void Names_find_their_swatch(string name, string hex) =>
        Assert.Equal(hex, WorkspacePalette.Named(name)?.Hex);

    [Fact]
    public void An_unknown_name_finds_nothing() =>
        Assert.Null(WorkspacePalette.Named("chartreuse"));
}
