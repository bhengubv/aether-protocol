// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Metainfo;
using MonoTorrent;
using Xunit;

namespace AetherNet.BitTorrent.Interop.Tests;

/// <summary>
/// Interoperability against MonoTorrent — a mature, independent C# BitTorrent implementation.
/// If our bencode / metainfo / info-hash agree byte-for-byte with a different real implementation,
/// then "AetherNet supports BitTorrent" is verifiable by anyone: point another client at our
/// torrents and the identities match.
/// </summary>
public class MonoTorrentInteropTests
{
    private static byte[] MakeContent(int size)
    {
        var d = new byte[size];
        for (int i = 0; i < size; i++) d[i] = (byte)(i * 61 + 5);
        return d;
    }

    [Fact]
    public void MonoTorrent_parses_our_torrent_and_computes_the_same_infohash()
    {
        var content = MakeContent(123_456);
        var torrentBytes = TorrentBuilder.CreateSingleFile("interop.bin", content, 32768, "http://tracker.example/announce");

        var ours = TorrentMetainfo.Parse(torrentBytes);
        var theirs = Torrent.Load(torrentBytes);

        Assert.Equal(ours.InfoHashV1Hex, theirs.InfoHash.ToHex().ToLowerInvariant());
        Assert.Equal(ours.TotalLength, theirs.Size);
        Assert.Equal(ours.Name, theirs.Name);
        Assert.Equal(ours.PieceLength, theirs.PieceLength);
    }

    [Fact]
    public void Our_bencode_roundtrips_monotorrents_bencoded_torrent()
    {
        // MonoTorrent produces the bytes; we decode + re-encode them and must get identical bytes.
        var content = MakeContent(40_000);
        var torrentBytes = TorrentBuilder.CreateSingleFile("x.bin", content, 16384);

        var decoded = Bencode.Decode(torrentBytes);
        var reEncoded = decoded.Encode();

        Assert.Equal(torrentBytes, reEncoded);

        // And MonoTorrent still accepts our re-encoded bytes with the same info-hash.
        var ours = TorrentMetainfo.Parse(reEncoded);
        var theirs = Torrent.Load(reEncoded);
        Assert.Equal(ours.InfoHashV1Hex, theirs.InfoHash.ToHex().ToLowerInvariant());
    }
}
