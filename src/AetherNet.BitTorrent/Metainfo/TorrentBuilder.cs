// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;

namespace AetherNet.BitTorrent.Metainfo;

/// <summary>
/// Creates BitTorrent v1 metainfo (<c>.torrent</c>) bytes from content. Produces a canonical
/// bencoded dictionary whose <c>info</c> dictionary carries SHA-1 piece hashes, so the resulting
/// info-hash matches what any BitTorrent client computes for the same content and piece length.
/// Used by the mesh gateway to re-seed AetherNet content out into a BitTorrent swarm.
/// </summary>
public static class TorrentBuilder
{
    /// <summary>
    /// Build a single-file <c>.torrent</c> for <paramref name="data"/>: split into
    /// <paramref name="pieceLength"/>-byte pieces, SHA-1-hash each, and bencode the metainfo.
    /// </summary>
    public static byte[] CreateSingleFile(string name, ReadOnlySpan<byte> data, int pieceLength, string? announce = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        if (pieceLength <= 0) throw new ArgumentOutOfRangeException(nameof(pieceLength), "piece length must be positive");

        int pieceCount = (int)((data.Length + (long)pieceLength - 1) / pieceLength);
        var pieces = new byte[pieceCount * 20];
        for (int i = 0; i < pieceCount; i++)
        {
            long start = (long)i * pieceLength;
            int len = (int)Math.Min(pieceLength, data.Length - start);
            Span<byte> hash = stackalloc byte[20];
            SHA1.HashData(data.Slice((int)start, len), hash);
            hash.CopyTo(pieces.AsSpan(i * 20, 20));
        }

        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(data.Length));
        info.Add("name", new BencodeString(name));
        info.Add("piece length", new BencodeInteger(pieceLength));
        info.Add("pieces", new BencodeString(pieces));

        var root = new BencodeDictionary();
        if (!string.IsNullOrWhiteSpace(announce)) root.Add("announce", new BencodeString(announce!));
        root.Add("info", info);

        return root.Encode();
    }
}
