// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Text.RegularExpressions;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The first thing anybody ever sees of this network.
///
/// <para>
/// It is read by somebody who has never heard of Aether, on a phone with nothing installed, after a
/// friend touched it against theirs. There is no second impression and no support channel — if the
/// page is broken or reads as something to be frightened of, that is the whole product for that
/// person.
/// </para>
/// </summary>
public class ShareCardTests
{
    private const string Download = "/tmb/00112233445566778899aabbccddeeff/aether.apk";

    private static string Card(string? from = "KXJB7-MN2P4", long size = 99_413_902) =>
        ShareCard.Render(from, size, Download);

    [Fact]
    public void It_says_who_is_offering_what_it_is_and_how_big()
    {
        var html = Card();

        Assert.Contains("KXJB7-MN2P4", html, StringComparison.Ordinal);
        Assert.Contains("Aether", html, StringComparison.Ordinal);
        Assert.Contains("94.8 MB", html, StringComparison.Ordinal);
        Assert.Contains(Download, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_on_it_comes_from_the_internet()
    {
        // The single hardest constraint here, and the easiest to break by habit. The person reading
        // this has no internet — that is the entire premise — so a web font, a hosted stylesheet or a
        // logo somewhere else would not load slowly, it would not load at all, and the first thing
        // they ever saw of this network would be a broken page.
        var html = Card();

        foreach (var offender in new[] { "http://", "https://", "//fonts.", "cdn.", "<img", "@import" })
            Assert.DoesNotContain(offender, html, StringComparison.OrdinalIgnoreCase);

        // The only src/href in the page is the package on this same phone.
        var links = Regex.Matches(html, @"(?:src|href)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        Assert.All(links, m => Assert.StartsWith("/", m.Groups[1].Value, StringComparison.Ordinal));
    }

    [Fact]
    public void It_prepares_them_for_the_warning_their_phone_will_show()
    {
        // The unknown-sources prompt is the single most likely moment for somebody to stop. It is far
        // less alarming when the page a friend sent them said it was coming.
        Assert.Contains("ask", Card(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_says_where_the_app_is_actually_coming_from()
    {
        // The reassurance that does the real work: this is the handset next to you, not a download
        // off the open internet from an address you have never seen.
        Assert.Contains("phone next to you", Card(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_still_works_when_the_giver_has_no_tag_yet()
    {
        // A phone whose identity has not come up must still be able to hand the app over.
        var html = Card(from: null);

        Assert.Contains(Download, html, StringComparison.Ordinal);
        Assert.DoesNotContain("from <span", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;")]
    [InlineData("\" onload=\"x", "&quot;")]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    public void Anything_that_reaches_the_page_is_escaped(string nasty, string expected)
    {
        // What lands here is this device's own tag, so nothing hostile is expected — but "nothing
        // hostile is expected" is exactly how a page ends up building markup out of a string nobody
        // checked.
        var html = ShareCard.Render(nasty, 1024 * 1024, Download);

        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_a_page_a_phone_will_render_the_way_it_was_drawn()
    {
        var html = Card();

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("charset=\"utf-8\"", html, StringComparison.Ordinal);
        // Without this a phone renders it at desktop width and shrinks it to unreadable.
        Assert.Contains("width=device-width", html, StringComparison.Ordinal);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_small_enough_to_arrive_instantly()
    {
        // Served off a phone over a local network to somebody standing next to you. Every kilobyte
        // here is a kilobyte of waiting in the one moment that has to feel effortless.
        Assert.True(Card().Length < 8 * 1024, $"the page is {Card().Length} bytes");
    }

    [Theory]
    [InlineData(0L, "")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(99_413_902L, "94.8 MB")]
    public void A_size_is_written_the_way_a_person_reads_it(long bytes, string expected)
        => Assert.Equal(expected, ShareCard.Size(bytes));

    [Fact]
    public void A_page_with_nowhere_to_send_them_is_refused()
        => Assert.Throws<ArgumentException>(() => ShareCard.Render("TAG", 1024, ""));
}
