// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;

namespace AetherNet.BitTorrent.Metainfo;

/// <summary>Thrown when a <c>.torrent</c> / magnet is structurally invalid.</summary>
public sealed class TorrentException : Exception
{
    public TorrentException(string message) : base(message) { }
}

/// <summary>One file within a torrent: its path components and length in bytes.</summary>
public sealed record TorrentFileEntry(IReadOnlyList<string> Path, long Length)
{
    /// <summary>Path components joined with '/'.</summary>
    public string JoinedPath => string.Join('/', Path);
}

/// <summary>
/// A parsed BitTorrent v1 metainfo (<c>.torrent</c>).
///
/// <para>The <see cref="InfoHashV1"/> is computed from the <em>raw</em> bencoded bytes of the
/// <c>info</c> dictionary as they appear in the file — NOT from a re-encode — so it matches real
/// clients byte-for-byte even when a torrent's dictionaries aren't canonically ordered.</para>
/// </summary>
public sealed class TorrentMetainfo
{
    public BencodeDictionary Root { get; }
    public BencodeDictionary Info { get; }

    /// <summary>20-byte SHA-1 of the raw bencoded info dictionary (the BitTorrent v1 info-hash).</summary>
    public byte[] InfoHashV1 { get; }

    public string Name { get; }
    public long PieceLength { get; }

    /// <summary>Each entry is a 20-byte SHA-1 piece hash (v1).</summary>
    public IReadOnlyList<byte[]> PieceHashes { get; }

    public IReadOnlyList<TorrentFileEntry> Files { get; }
    public long TotalLength { get; }
    public IReadOnlyList<string> AnnounceUrls { get; }
    public bool IsSingleFile { get; }

    /// <summary>Lowercase hex of <see cref="InfoHashV1"/> (40 chars).</summary>
    public string InfoHashV1Hex => Convert.ToHexString(InfoHashV1).ToLowerInvariant();

    private TorrentMetainfo(
        BencodeDictionary root, BencodeDictionary info, byte[] infoHashV1, string name,
        long pieceLength, IReadOnlyList<byte[]> pieceHashes, IReadOnlyList<TorrentFileEntry> files,
        long totalLength, IReadOnlyList<string> announceUrls, bool isSingleFile)
    {
        Root = root;
        Info = info;
        InfoHashV1 = infoHashV1;
        Name = name;
        PieceLength = pieceLength;
        PieceHashes = pieceHashes;
        Files = files;
        TotalLength = totalLength;
        AnnounceUrls = announceUrls;
        IsSingleFile = isSingleFile;
    }

    public static TorrentMetainfo Parse(byte[] torrent) => Parse((torrent ?? throw new ArgumentNullException(nameof(torrent))).AsSpan());

    public static TorrentMetainfo Parse(ReadOnlySpan<byte> torrent)
    {
        var root = Bencode.Decode(torrent).AsDictionary();

        var info = (root["info"] ?? throw new TorrentException("metainfo has no 'info' dictionary")).AsDictionary();

        // Info-hash from the RAW info bytes (canonicalisation-independent, matches real clients).
        var infoHash = SHA1.HashData(ExtractInfoSpan(torrent));

        string name = (info["name"] ?? throw new TorrentException("info has no 'name'")).AsText();

        long pieceLength = (info["piece length"] ?? throw new TorrentException("info has no 'piece length'")).AsInteger();
        if (pieceLength <= 0) throw new TorrentException("'piece length' must be positive");

        var piecesBytes = (info["pieces"] ?? throw new TorrentException("info has no 'pieces'")).AsBytes();
        if (piecesBytes.Length % 20 != 0)
            throw new TorrentException($"'pieces' length {piecesBytes.Length} is not a multiple of 20");
        var pieceHashes = new List<byte[]>(piecesBytes.Length / 20);
        for (int i = 0; i < piecesBytes.Length; i += 20)
            pieceHashes.Add(piecesBytes[i..(i + 20)]);

        var files = new List<TorrentFileEntry>();
        long total = 0;
        bool singleFile;
        if (info["files"] is { } filesValue)
        {
            singleFile = false;
            foreach (var f in filesValue.AsList())
            {
                var fd = f.AsDictionary();
                long len = (fd["length"] ?? throw new TorrentException("file entry has no 'length'")).AsInteger();
                var parts = (fd["path"] ?? throw new TorrentException("file entry has no 'path'"))
                    .AsList().Select(p => p.AsText()).ToList();
                if (parts.Count == 0) throw new TorrentException("file entry has an empty 'path'");
                files.Add(new TorrentFileEntry(parts, len));
                total += len;
            }
        }
        else
        {
            singleFile = true;
            long len = (info["length"] ?? throw new TorrentException("single-file info has neither 'length' nor 'files'")).AsInteger();
            files.Add(new TorrentFileEntry(new[] { name }, len));
            total = len;
        }

        // Trackers: announce + announce-list, de-duplicated, order preserved.
        var announce = new List<string>();
        var seen = new HashSet<string>();
        void Add(string url) { if (!string.IsNullOrWhiteSpace(url) && seen.Add(url)) announce.Add(url); }
        if (root["announce"] is { } a) Add(a.AsText());
        if (root["announce-list"] is { } al)
            foreach (var tier in al.AsList())
                foreach (var t in tier.AsList())
                    Add(t.AsText());

        return new TorrentMetainfo(root, info, infoHash, name, pieceLength, pieceHashes, files, total, announce, singleFile);
    }

    /// <summary>
    /// Return the raw bencoded bytes of the <c>info</c> value by walking the top-level dictionary
    /// with byte-offset tracking (structure already validated by the full decode).
    /// </summary>
    private static ReadOnlySpan<byte> ExtractInfoSpan(ReadOnlySpan<byte> torrent)
    {
        if (torrent.Length == 0 || torrent[0] != (byte)'d')
            throw new TorrentException("metainfo is not a bencoded dictionary");
        int pos = 1;
        while (pos < torrent.Length && torrent[pos] != (byte)'e')
        {
            var key = Bencode.Decode(torrent[pos..], out int keyConsumed).AsBytes();
            pos += keyConsumed;
            int valueStart = pos;
            Bencode.Decode(torrent[pos..], out int valueConsumed);
            int valueEnd = pos + valueConsumed;
            pos = valueEnd;
            if (key.AsSpan().SequenceEqual("info"u8))
                return torrent[valueStart..valueEnd];
        }
        throw new TorrentException("metainfo has no 'info' key");
    }
}
