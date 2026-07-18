// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Metainfo;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class MetainfoTests
{
    private static BencodeString Str(string s) => new(s);

    [Fact]
    public void Parses_single_file_torrent_and_computes_infohash()
    {
        var pieces = new byte[40]; // two 20-byte piece hashes
        for (int i = 0; i < pieces.Length; i++) pieces[i] = (byte)i;

        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(2048));
        info.Add("name", Str("hello.txt"));
        info.Add("piece length", new BencodeInteger(1024));
        info.Add("pieces", new BencodeString(pieces));

        var root = new BencodeDictionary();
        root.Add("announce", Str("udp://tracker.example:6969/announce"));
        root.Add("info", info);

        var t = TorrentMetainfo.Parse(root.Encode());

        Assert.True(t.IsSingleFile);
        Assert.Equal("hello.txt", t.Name);
        Assert.Equal(1024, t.PieceLength);
        Assert.Equal(2, t.PieceHashes.Count);
        Assert.Equal(2048, t.TotalLength);
        Assert.Single(t.Files);
        Assert.Equal("hello.txt", t.Files[0].JoinedPath);
        Assert.Equal(2048, t.Files[0].Length);
        Assert.Equal(new[] { "udp://tracker.example:6969/announce" }, t.AnnounceUrls);

        // For a canonically-encoded torrent, the info-hash is SHA-1 of the canonical info bytes.
        Assert.Equal(SHA1.HashData(info.Encode()), t.InfoHashV1);
        Assert.Equal(40, t.InfoHashV1Hex.Length);
    }

    [Fact]
    public void Parses_multi_file_torrent()
    {
        var info = new BencodeDictionary();
        info.Add("name", Str("mydir"));
        info.Add("piece length", new BencodeInteger(16384));
        info.Add("pieces", new BencodeString(new byte[20]));

        var f1 = new BencodeDictionary();
        f1.Add("length", new BencodeInteger(100));
        f1.Add("path", new BencodeList(new BencodeValue[] { Str("a"), Str("x.txt") }));
        var f2 = new BencodeDictionary();
        f2.Add("length", new BencodeInteger(200));
        f2.Add("path", new BencodeList(new BencodeValue[] { Str("y.txt") }));
        info.Add("files", new BencodeList(new BencodeValue[] { f1, f2 }));

        var root = new BencodeDictionary();
        root.Add("info", info);

        var t = TorrentMetainfo.Parse(root.Encode());

        Assert.False(t.IsSingleFile);
        Assert.Equal(2, t.Files.Count);
        Assert.Equal("a/x.txt", t.Files[0].JoinedPath);
        Assert.Equal(100, t.Files[0].Length);
        Assert.Equal("y.txt", t.Files[1].JoinedPath);
        Assert.Equal(300, t.TotalLength);
    }

    [Fact]
    public void Flattens_announce_list_and_dedups()
    {
        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(1));
        info.Add("name", Str("z"));
        info.Add("piece length", new BencodeInteger(1));
        info.Add("pieces", new BencodeString(new byte[20]));

        var root = new BencodeDictionary();
        root.Add("announce", Str("udp://a/x"));
        root.Add("announce-list", new BencodeList(new BencodeValue[]
        {
            new BencodeList(new BencodeValue[] { Str("udp://a/x") }),      // duplicate of announce
            new BencodeList(new BencodeValue[] { Str("http://b/y") }),
        }));
        root.Add("info", info);

        var t = TorrentMetainfo.Parse(root.Encode());
        Assert.Equal(new[] { "udp://a/x", "http://b/y" }, t.AnnounceUrls);
    }

    [Fact]
    public void Infohash_uses_raw_info_bytes_not_a_reencode()
    {
        // Hand-build a NON-canonical info dict (keys out of sorted order: name before length).
        // A correct info-hash must be SHA-1 of these raw bytes — not of a canonicalised re-encode.
        byte[] Cat(params byte[][] parts)
        {
            var outp = new List<byte>();
            foreach (var p in parts) outp.AddRange(p);
            return outp.ToArray();
        }
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        var infoRaw = Cat(
            Ascii("d"),
            Ascii("4:name1:z"),            // name first (non-canonical)
            Ascii("6:lengthi1e"),
            Ascii("12:piece lengthi1e"),
            Ascii("6:pieces20:"), new byte[20],
            Ascii("e"));
        var torrentRaw = Cat(Ascii("d4:info"), infoRaw, Ascii("e"));

        var t = TorrentMetainfo.Parse(torrentRaw);

        // Info-hash is over the exact raw bytes as written...
        Assert.Equal(SHA1.HashData(infoRaw), t.InfoHashV1);
        // ...and is DIFFERENT from a canonicalised re-encode (which reorders the keys).
        Assert.NotEqual(SHA1.HashData(t.Info.Encode()), t.InfoHashV1);
    }

    [Fact]
    public void Rejects_missing_info() =>
        Assert.Throws<TorrentException>(() => TorrentMetainfo.Parse(new BencodeDictionary().Encode()));

    [Fact]
    public void Rejects_bad_pieces_length()
    {
        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(1));
        info.Add("name", Str("z"));
        info.Add("piece length", new BencodeInteger(1));
        info.Add("pieces", new BencodeString(new byte[19])); // not a multiple of 20
        var root = new BencodeDictionary();
        root.Add("info", info);
        Assert.Throws<TorrentException>(() => TorrentMetainfo.Parse(root.Encode()));
    }
}
