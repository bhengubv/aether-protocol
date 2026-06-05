// SPDX-License-Identifier: MIT

using AetherMesh.Fmhy;
using Xunit;

namespace AetherMesh.Fmhy.Tests;

public sealed class FmhyMarkdownParserTests
{
    // ── Minimal fixture markdown ──────────────────────────────────────────────

    private const string MinimalMarkdown = """
        # Video Tools

        * ⭐ **[mpv](https://mpv.io/)** - Free, open-source, cross-platform media player.
        * **[VLC](https://www.videolan.org/)** - Versatile open-source media player.

        # Torrent Clients

        * ⭐ **[qBittorrent](https://www.qbittorrent.org/)**, [GitHub](https://github.com/qbittorrent/qBittorrent) - Free BitTorrent client.
        * **[Deluge](https://deluge-torrent.org/)** - Lightweight BitTorrent client.

        ## Advanced

        * ⭐ **[WebTorrent](https://webtorrent.io/)**, [GitHub](https://github.com/webtorrent/webtorrent) - BitTorrent for the web.
        """;

    // ── Parser correctness ────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReturnsExpectedEntryCount()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void Parse_ExtractsNameAndUrl()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        var mpv = entries[0];
        Assert.Equal("mpv",              mpv.Name);
        Assert.Equal("https://mpv.io/", mpv.Url);
    }

    [Fact]
    public void Parse_StarredEntries_DetectedCorrectly()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        // mpv, qBittorrent, WebTorrent are starred; VLC, Deluge are not.
        Assert.Equal(3, entries.Count(e => e.IsStarred));
        Assert.True(entries.Single(e => e.Name == "mpv").IsStarred);
        Assert.False(entries.Single(e => e.Name == "VLC").IsStarred);
    }

    [Fact]
    public void Parse_CategoryFollowsH1Heading()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        Assert.Equal("Video Tools", entries[0].Category);
        Assert.Equal("Video Tools", entries[1].Category);
    }

    [Fact]
    public void Parse_H2SubcategoryAppendedToH1()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        // WebTorrent is under "# Torrent Clients / ## Advanced"
        var webTorrent = entries.Single(e => e.Name == "WebTorrent");
        Assert.Equal("Torrent Clients / Advanced", webTorrent.Category);
    }

    [Fact]
    public void Parse_DescriptionExtracted()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        var mpv = entries[0];
        Assert.NotNull(mpv.Description);
        Assert.Contains("media player", mpv.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MirrorsExtracted()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        // qBittorrent has a GitHub mirror link
        var qbt = entries.Single(e => e.Name == "qBittorrent");
        Assert.Contains("https://github.com/qbittorrent/qBittorrent", qbt.Mirrors);
    }

    [Fact]
    public void Parse_WebTorrent_HasCommaStyleMirror()
    {
        var entries = FmhyMarkdownParser.Parse(MinimalMarkdown);
        var wt = entries.Single(e => e.Name == "WebTorrent");
        Assert.Contains("https://github.com/webtorrent/webtorrent", wt.Mirrors);
    }

    [Fact]
    public void Parse_EmptyMarkdown_ReturnsEmpty()
    {
        var entries = FmhyMarkdownParser.Parse(string.Empty);
        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_NoHeadings_EntriesHaveEmptyCategory()
    {
        const string noHeadings = "* ⭐ **[mpv](https://mpv.io/)** - Player.";
        var entries = FmhyMarkdownParser.Parse(noHeadings);
        Assert.Single(entries);
        Assert.Equal(string.Empty, entries[0].Category);
    }

    [Fact]
    public void Parse_LinesWithoutBoldLink_Skipped()
    {
        const string md = """
            # Tools

            Some introductory text that is not a bullet.
            * Plain bullet without bold link — skip me.
            * ⭐ **[qBittorrent](https://www.qbittorrent.org/)** - Torrent client.
            """;
        var entries = FmhyMarkdownParser.Parse(md);
        Assert.Single(entries);
        Assert.Equal("qBittorrent", entries[0].Name);
    }
}
