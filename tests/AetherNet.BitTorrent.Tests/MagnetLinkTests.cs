// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Metainfo;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class MagnetLinkTests
{
    [Fact]
    public void Parses_hex_infohash_with_name_and_trackers()
    {
        var hash = new byte[20];
        for (int i = 0; i < 20; i++) hash[i] = (byte)(i * 7);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        var m = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{hex}&dn=My%20File&tr=udp%3A%2F%2Ftr1%3A80&tr=http%3A%2F%2Ftr2%2Fann");

        Assert.Equal(hash, m.InfoHashV1);
        Assert.Equal("My File", m.DisplayName);
        Assert.Equal(new[] { "udp://tr1:80", "http://tr2/ann" }, m.Trackers);
    }

    [Fact]
    public void Parses_base32_infohash()
    {
        // 20 zero bytes → base32 is 32 'A's.
        var m = MagnetLink.Parse("magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(new byte[20], m.InfoHashV1);
    }

    [Fact]
    public void Rejects_non_magnet() =>
        Assert.Throws<TorrentException>(() => MagnetLink.Parse("http://example.com"));

    [Fact]
    public void Rejects_magnet_without_infohash() =>
        Assert.Throws<TorrentException>(() => MagnetLink.Parse("magnet:?dn=noHashHere"));

    [Fact]
    public void Rejects_bad_infohash_length() =>
        Assert.Throws<TorrentException>(() => MagnetLink.Parse("magnet:?xt=urn:btih:tooShort"));

    [Fact]
    public void Torrent_and_magnet_agree_on_infohash()
    {
        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(1));
        info.Add("name", new BencodeString("z"));
        info.Add("piece length", new BencodeInteger(1));
        info.Add("pieces", new BencodeString(new byte[20]));
        var root = new BencodeDictionary();
        root.Add("info", info);

        var t = TorrentMetainfo.Parse(root.Encode());
        var m = MagnetLink.Parse($"magnet:?xt=urn:btih:{t.InfoHashV1Hex}");

        Assert.Equal(t.InfoHashV1, m.InfoHashV1);
    }
}
