// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.PeerWire;

namespace AetherNet.BitTorrent.Storage;

/// <summary>
/// Holds a torrent's pieces in memory, keyed by piece index. Each stored piece has been verified
/// against its SHA-1 (v1). Serves blocks to peers and, once complete, reassembles the whole content.
/// </summary>
public sealed class PieceStore
{
    private readonly byte[]?[] _pieces;
    private readonly byte[][] _hashes;

    public long TotalLength { get; }
    public int PieceLength { get; }
    public int PieceCount => _pieces.Length;
    public IReadOnlyList<byte[]> PieceHashes => _hashes;

    public PieceStore(long totalLength, int pieceLength, IReadOnlyList<byte[]> pieceHashes)
    {
        if (totalLength < 0) throw new ArgumentOutOfRangeException(nameof(totalLength));
        if (pieceLength <= 0) throw new ArgumentOutOfRangeException(nameof(pieceLength));
        ArgumentNullException.ThrowIfNull(pieceHashes);

        TotalLength = totalLength;
        PieceLength = pieceLength;
        _hashes = pieceHashes.Select(h => (byte[])h.Clone()).ToArray();

        int expected = (int)((totalLength + pieceLength - 1) / pieceLength);
        if (_hashes.Length != expected)
            throw new ArgumentException($"expected {expected} piece hashes for {totalLength} bytes / {pieceLength}, got {_hashes.Length}");
        _pieces = new byte[_hashes.Length][];
    }

    public int LengthOfPiece(int index)
    {
        if ((uint)index >= (uint)PieceCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (index < PieceCount - 1) return PieceLength;
        return (int)(TotalLength - (long)(PieceCount - 1) * PieceLength);
    }

    public bool Has(int index) => _pieces[index] is not null;

    /// <summary>Read a block of a piece we hold (for serving a peer's request).</summary>
    public byte[] ReadBlock(int index, int begin, int length)
    {
        var piece = _pieces[index] ?? throw new InvalidOperationException($"piece {index} is not present");
        if (begin < 0 || length < 0 || begin + length > piece.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "block is outside the piece");
        return piece[begin..(begin + length)];
    }

    /// <summary>Verify assembled piece data against its hash and, if it matches, store it.</summary>
    public bool TryComplete(int index, byte[] data)
    {
        if (data.Length != LengthOfPiece(index)) return false;
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(_hashes[index])) return false;
        _pieces[index] = data;
        return true;
    }

    public Bitfield BuildBitfield()
    {
        var bf = new Bitfield(PieceCount);
        for (int i = 0; i < PieceCount; i++)
            if (Has(i)) bf[i] = true;
        return bf;
    }

    public bool IsComplete
    {
        get
        {
            for (int i = 0; i < PieceCount; i++)
                if (!Has(i)) return false;
            return true;
        }
    }

    public byte[] Assemble()
    {
        if (!IsComplete) throw new InvalidOperationException("content is incomplete");
        var output = new byte[TotalLength];
        long offset = 0;
        for (int i = 0; i < PieceCount; i++)
        {
            var piece = _pieces[i]!;
            piece.CopyTo(output, (int)offset);
            offset += piece.Length;
        }
        return output;
    }

    /// <summary>Build a fully-populated store from content (a seeder), computing each piece's SHA-1.</summary>
    public static PieceStore FromContent(byte[] content, int pieceLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (pieceLength <= 0) throw new ArgumentOutOfRangeException(nameof(pieceLength));

        int count = (int)((content.Length + (long)pieceLength - 1) / pieceLength);
        var hashes = new byte[count][];
        var pieces = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int begin = i * pieceLength;
            int len = Math.Min(pieceLength, content.Length - begin);
            var piece = content[begin..(begin + len)];
            pieces[i] = piece;
            hashes[i] = SHA1.HashData(piece);
        }

        var store = new PieceStore(content.Length, pieceLength, hashes);
        for (int i = 0; i < count; i++) store._pieces[i] = pieces[i];
        return store;
    }
}
