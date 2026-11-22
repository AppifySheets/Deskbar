using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// Which part of the wallpaper image sits behind the docked bar, for each way Windows can lay a picture on a screen.
// The numbers are Petre's desk: a 3840 x 2160 screen, the bar docked on its right edge 316 px wide, and a 3840 x 2160
// Spotlight picture, plus a few pictures of other shapes to make the scaling visible.
public class WallpaperCropTests
{
    static readonly WallpaperCrop.Rect Screen = new(-3840, 286, 0, 2446);
    static readonly WallpaperCrop.Rect Desktop = new(-3840, 0, 3840, 2446);
    static readonly WallpaperCrop.Rect Bar = new(-316, 286, 0, 2446);

    static WallpaperCrop.Area Crop(WallpaperFit fit, int imageWidth, int imageHeight, WallpaperCrop.Rect? bar = null) =>
        WallpaperCrop.SourceOf(fit, Screen, Desktop, imageWidth, imageHeight, bar ?? Bar).Value;

    static void Near(WallpaperCrop.Area expected, WallpaperCrop.Area actual)
    {
        Assert.Equal(expected.X, actual.X, 1);
        Assert.Equal(expected.Y, actual.Y, 1);
        Assert.Equal(expected.Width, actual.Width, 1);
        Assert.Equal(expected.Height, actual.Height, 1);
    }

    // A picture the size of the screen: every fit but tile puts pixel for pixel, so the bar's strip is the picture's.
    [Theory]
    [InlineData(WallpaperFit.Fill)]
    [InlineData(WallpaperFit.Fit)]
    [InlineData(WallpaperFit.Stretch)]
    [InlineData(WallpaperFit.Center)]
    public void A_picture_the_size_of_the_screen_gives_the_bars_strip_of_it(WallpaperFit fit) =>
        Near(new(3840 - 316, 0, 316, 2160), Crop(fit, 3840, 2160));

    // Fill scales up until both sides are covered and cuts the overflow evenly. A 1920 x 1200 picture on a 16:9 screen
    // scales by 2 (3840 / 1920), so it is 2400 tall and 120 px of it hang over the top and the bottom.
    [Fact]
    public void Fill_scales_to_cover_and_centres_the_overflow() =>
        Near(new(1920 - 158, 60, 158, 1080), Crop(WallpaperFit.Fill, 1920, 1200));

    // Stretch scales each side on its own, so the picture's whole height is behind the bar.
    [Fact]
    public void Stretch_scales_each_side_on_its_own() =>
        Near(new(1920 - 158, 0, 158, 1200), Crop(WallpaperFit.Stretch, 1920, 1200));

    // Span lays one picture across the whole desktop, so the bar at the right edge of the LEFT screen sees the middle
    // of a picture twice the screen's width.
    [Fact]
    public void Span_lays_the_picture_across_every_screen()
    {
        // 7680 x 2446 desktop, picture 7680 x 2446: one to one. The bar is at desktop x 3524..3840, y 286..2446.
        Near(new(3524, 286, 316, 2160), Crop(WallpaperFit.Span, 7680, 2446));
    }

    // Fit leaves bars of background colour beside a picture of another shape. A square picture on a 16:9 screen is
    // 2160 wide in the middle, so the screen's right edge is background colour, and there is nothing to frost.
    [Fact]
    public void Fit_gives_nothing_where_the_bar_is_over_the_background_colour() =>
        Assert.True(WallpaperCrop.SourceOf(WallpaperFit.Fit, Screen, Desktop, 1000, 1000, Bar).HasNoValue);

    // Centre at one to one: a picture smaller than the screen does not reach its edge.
    [Fact]
    public void Center_gives_nothing_where_a_small_picture_does_not_reach() =>
        Assert.True(WallpaperCrop.SourceOf(WallpaperFit.Center, Screen, Desktop, 1920, 1080, Bar).HasNoValue);

    // Tile repeats the picture, so one rectangle of it cannot say what is behind the bar.
    [Fact]
    public void Tile_is_not_frosted() =>
        Assert.True(WallpaperCrop.SourceOf(WallpaperFit.Tile, Screen, Desktop, 3840, 2160, Bar).HasNoValue);

    // A bar that is not on the screen it is measured against (a screen waking, a stale answer) is not guessed at.
    [Fact]
    public void A_bar_off_its_screen_is_not_frosted() =>
        Assert.True(WallpaperCrop.SourceOf(WallpaperFit.Fill, Screen, Desktop, 3840, 2160, new(10, 0, 326, 2160)).HasNoValue);

    // A picture with no size (a file that would not decode) is not divided by.
    [Fact]
    public void A_picture_with_no_size_is_not_frosted() =>
        Assert.True(WallpaperCrop.SourceOf(WallpaperFit.Fill, Screen, Desktop, 0, 0, Bar).HasNoValue);
}
