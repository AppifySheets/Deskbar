using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Tests.Bookmarks;

// The letter tile and the favicon cache key.
public sealed class LetterTileTests
{
    [Theory]
    [InlineData("portainer", 'P')]
    [InlineData("  9gag", '9')]
    [InlineData("--", '?')]
    [InlineData("", '?')]
    public void The_tile_letter_is_the_first_letter_of_the_name(string name, char expected) =>
        Assert.Equal(expected, LetterTile.Of(name, "x.local").Letter);

    [Fact]
    public void The_tile_colour_is_the_same_every_time_for_a_host() =>
        Assert.Equal(LetterTile.Of("a", "Portainer.local").Hue, LetterTile.Of("b", "portainer.LOCAL").Hue);

    [Fact]
    public void The_tile_hue_is_a_degree() =>
        Assert.InRange(LetterTile.Of("a", "portainer.local").Hue, 0, 359.999);

    [Fact]
    public void Two_hosts_get_different_colours() =>
        Assert.NotEqual(LetterTile.Of("a", "portainer.local").Hue, LetterTile.Of("a", "grafana.local").Hue);

    [Fact]
    public void The_cache_key_is_the_origin() =>
        Assert.Equal("https_portainer.local_9443", FaviconKey.Of(new Uri("https://Portainer.local:9443/x")));

    [Fact]
    public void An_ipv6_origin_gives_a_valid_file_name() =>
        Assert.Equal("http_--1_8080", FaviconKey.Of(new Uri("http://[::1]:8080/")));
}
