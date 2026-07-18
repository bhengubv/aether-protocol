// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;

namespace AetherNet.BitTorrent.Extensions;

public enum MetadataMessageType
{
    Request = 0,
    Data = 1,
    Reject = 2,
}

/// <summary>
/// ut_metadata (BEP-9): fetches a torrent's metadata (the info dictionary) from peers in 16 KiB
/// pieces — the mechanism that turns a magnet link's bare info-hash into a real torrent. Each message
/// is a bencoded control dict; a <see cref="MetadataMessageType.Data"/> message appends the raw piece
/// bytes after the dict.
/// </summary>
public static class UtMetadata
{
    public const int PieceSize = 16384;

    public static byte[] BuildRequest(int piece) => Control(MetadataMessageType.Request, piece, null);
    public static byte[] BuildReject(int piece) => Control(MetadataMessageType.Reject, piece, null);

    public static byte[] BuildData(int piece, int totalSize, byte[] pieceData)
    {
        ArgumentNullException.ThrowIfNull(pieceData);
        var header = Control(MetadataMessageType.Data, piece, totalSize);
        var buf = new byte[header.Length + pieceData.Length];
        header.CopyTo(buf, 0);
        pieceData.CopyTo(buf, header.Length);
        return buf;
    }

    private static byte[] Control(MetadataMessageType type, int piece, int? totalSize)
    {
        var d = new BencodeDictionary();
        d.Add("msg_type", new BencodeInteger((int)type));
        d.Add("piece", new BencodeInteger(piece));
        if (totalSize is { } t) d.Add("total_size", new BencodeInteger(t));
        return d.Encode();
    }

    public static (MetadataMessageType Type, int Piece, int? TotalSize, byte[] Data) Parse(ReadOnlySpan<byte> body)
    {
        var dict = Bencode.Decode(body, out int consumed).AsDictionary();
        var type = (MetadataMessageType)(int)(dict["msg_type"] ?? throw new BencodeException("ut_metadata: no msg_type")).AsInteger();
        int piece = (int)(dict["piece"] ?? throw new BencodeException("ut_metadata: no piece")).AsInteger();
        int? total = dict["total_size"] is { } t ? (int)t.AsInteger() : null;
        var data = body[consumed..].ToArray(); // trailing raw piece bytes (Data messages only)
        return (type, piece, total, data);
    }
}

/// <summary>Reassembles ut_metadata pieces into the info dictionary and verifies it against the info-hash.</summary>
public sealed class MetadataAssembler
{
    private readonly byte[] _buffer;
    private readonly bool[] _have;

    public int TotalSize { get; }
    public int PieceCount { get; }

    public MetadataAssembler(int totalSize)
    {
        if (totalSize <= 0) throw new ArgumentOutOfRangeException(nameof(totalSize));
        TotalSize = totalSize;
        _buffer = new byte[totalSize];
        PieceCount = (totalSize + UtMetadata.PieceSize - 1) / UtMetadata.PieceSize;
        _have = new bool[PieceCount];
    }

    public int LengthOfPiece(int index) =>
        index < PieceCount - 1 ? UtMetadata.PieceSize : TotalSize - (PieceCount - 1) * UtMetadata.PieceSize;

    public void AddPiece(int index, ReadOnlySpan<byte> data)
    {
        if ((uint)index >= (uint)PieceCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (data.Length != LengthOfPiece(index)) throw new ArgumentException("metadata piece has the wrong length");
        data.CopyTo(_buffer.AsSpan(index * UtMetadata.PieceSize));
        _have[index] = true;
    }

    public bool IsComplete => Array.TrueForAll(_have, h => h);

    /// <summary>The full info dictionary bytes if complete and its SHA-1 matches <paramref name="infoHash"/>; else null.</summary>
    public byte[]? TryFinish(byte[] infoHash)
    {
        if (!IsComplete) return null;
        return SHA1.HashData(_buffer).AsSpan().SequenceEqual(infoHash) ? (byte[])_buffer.Clone() : null;
    }
}
