using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Tests.Bookmarks;

// Which icon a bookmarked page declares, for the favicon fetch's second try (when <origin>/favicon.ico gave nothing
// usable). Only an icon on the page's own origin that WPF can decode is taken; anything else is no icon, and the
// letter tile stands.
public sealed class DeclaredIconTests
{
    static readonly Uri Page = new("https://100.65.0.11:9443/#!/home");

    static string Html(string head) => $"<!doctype html><html><head><title>Portainer</title>{head}</head><body>x</body></html>";

    [Fact]
    public void A_relative_href_is_resolved_against_the_page()
    {
        var icon = DeclaredIcon.Of(Html("""<link rel="icon" type="image/png" href="dist/favicon-32x32.png">"""), Page);

        Assert.Equal("https://100.65.0.11:9443/dist/favicon-32x32.png", icon.Value.AbsoluteUri);
    }

    [Fact]
    public void An_absolute_href_on_the_same_origin_is_taken()
    {
        var icon = DeclaredIcon.Of(Html("""<LINK REL='Shortcut Icon' HREF='https://100.65.0.11:9443/img/logo.ico'>"""), Page);

        Assert.Equal("https://100.65.0.11:9443/img/logo.ico", icon.Value.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://cdn.example.com/logo.png")] // another host
    [InlineData("http://100.65.0.11:9443/logo.png")] // another scheme
    [InlineData("https://100.65.0.11/logo.png")] // another port
    [InlineData("//cdn.example.com/logo.png")] // scheme-relative, another host
    [InlineData("data:image/png;base64,AAAA")] // not a request at all
    public void An_icon_on_another_origin_is_refused(string href)
    {
        Assert.True(DeclaredIcon.Of(Html($"""<link rel="icon" href="{href}">"""), Page).HasNoValue);
    }

    [Theory]
    [InlineData("""<link rel="icon" href="logo.svg">""")]
    [InlineData("""<link rel="icon" href="logo.SVG?v=2">""")]
    [InlineData("""<link rel="icon" type="image/svg+xml" href="logo">""")]
    public void An_svg_icon_is_refused(string link)
    {
        Assert.True(DeclaredIcon.Of(Html(link), Page).HasNoValue);
    }

    [Fact]
    public void The_first_usable_icon_in_page_order_wins()
    {
        var icon = DeclaredIcon.Of(Html("""
            <link rel="stylesheet" href="app.css">
            <link rel="mask-icon" href="pin.svg" color="#000">
            <link rel="apple-touch-icon" href="touch.png">
            <link rel="icon" href="later.png">
            """), Page);

        Assert.Equal("https://100.65.0.11:9443/touch.png", icon.Value.AbsoluteUri);
    }

    [Fact]
    public void A_page_without_an_icon_link_has_none()
    {
        Assert.True(DeclaredIcon.Of(Html("""<link rel="stylesheet" href="app.css"><meta name="icon" content="x.png">"""), Page).HasNoValue);
        Assert.True(DeclaredIcon.Of("", Page).HasNoValue);
        Assert.True(DeclaredIcon.Of("""<link rel="icon">""", Page).HasNoValue);
    }

    [Fact]
    public void Entities_in_the_href_are_decoded_and_the_attributes_may_come_in_any_order()
    {
        var icon = DeclaredIcon.Of(Html("""<link href="icon.png?a=1&amp;b=2" sizes="32x32" rel=icon>"""), Page);

        Assert.Equal("https://100.65.0.11:9443/icon.png?a=1&b=2", icon.Value.AbsoluteUri);
    }

    [Fact]
    public void The_page_request_drops_the_fragment_and_the_user_information()
    {
        Assert.Equal("https://100.65.0.11:9443/x?y=1", DeclaredIcon.PageOf(new Uri("https://me:secret@100.65.0.11:9443/x?y=1#!/home")).AbsoluteUri);
    }
}
