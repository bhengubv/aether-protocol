// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.Metainfo;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class TorrentBuilderTests
{
    [Fact]
    public void Built_torrent_parses_back_with_correct_fields_and_pieces()
    {
        var data = new byte[70000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        const int pieceLength = 32768;

        var bytes = TorrentBuilder.CreateSingleFile("payload.bin", data, pieceLength, "http://tracker.example/announce");
        var meta = TorrentMetainfo.Parse(bytes);

        Assert.Equal("payload.bin", meta.Name);
        Assert.Equal(data.Length, meta.TotalLength);
        Assert.Equal(pieceLength, meta.PieceLength);
        Assert.True(meta.IsSingleFile);
        Assert.Equal("http://tracker.example/announce", Assert.Single(meta.AnnounceUrls));

        int expectedPieces = (data.Length + pieceLength - 1) / pieceLength; // 70000 → 3 pieces
        Assert.Equal(expectedPieces, meta.PieceHashes.Count);
        for (int i = 0; i < expectedPieces; i++)
        {
            int start = i * pieceLength;
            int len = Math.Min(pieceLength, data.Length - start);
            var expected = SHA1.HashData(data.AsSpan(start, len).ToArray());
            Assert.Equal(expected, meta.PieceHashes[i]);
        }
    }

    [Fact]
    public void Info_hash_is_deterministic_for_identical_content()
    {
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;

        var a = TorrentMetainfo.Parse(TorrentBuilder.CreateSingleFile("f", data, 16384));
        var b = TorrentMetainfo.Parse(TorrentBuilder.CreateSingleFile("f", data, 16384));

        Assert.Equal(a.InfoHashV1Hex, b.InfoHashV1Hex);
        Assert.Equal(40, a.InfoHashV1Hex.Length);
    }
}
