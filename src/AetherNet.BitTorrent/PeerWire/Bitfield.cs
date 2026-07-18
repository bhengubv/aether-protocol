// SPDX-License-Identifier: MIT

namespace AetherNet.BitTorrent.PeerWire;

/// <summary>
/// A piece-availability bitfield (BEP-3): bit for piece <c>i</c> is the
/// <c>(0x80 &gt;&gt; (i%8))</c> bit of byte <c>i/8</c> — i.e. piece 0 is the high bit of byte 0.
/// </summary>
public sealed class Bitfield
{
    private readonly byte[] _bytes;

    /// <summary>Number of pieces (bits) this bitfield tracks.</summary>
    public int Count { get; }

    public int ByteLength => _bytes.Length;

    public Bitfield(int pieceCount)
    {
        if (pieceCount < 0) throw new ArgumentOutOfRangeException(nameof(pieceCount));
        Count = pieceCount;
        _bytes = new byte[(pieceCount + 7) / 8];
    }

    public Bitfield(byte[] bytes, int pieceCount)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (pieceCount < 0) throw new ArgumentOutOfRangeException(nameof(pieceCount));
        int expected = (pieceCount + 7) / 8;
        if (bytes.Length != expected)
            throw new PeerWireException($"bitfield for {pieceCount} pieces must be {expected} bytes, got {bytes.Length}");
        Count = pieceCount;
        _bytes = (byte[])bytes.Clone();
    }

    public bool this[int index]
    {
        get => Get(index);
        set => Set(index, value);
    }

    public bool Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return (_bytes[index >> 3] & (0x80 >> (index & 7))) != 0;
    }

    public void Set(int index, bool value = true)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        int mask = 0x80 >> (index & 7);
        if (value) _bytes[index >> 3] |= (byte)mask;
        else _bytes[index >> 3] &= (byte)~mask;
    }

    /// <summary>Number of set bits (pieces present).</summary>
    public int PopCount()
    {
        int n = 0;
        for (int i = 0; i < Count; i++)
            if (Get(i)) n++;
        return n;
    }

    public bool HasAll() => PopCount() == Count;

    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
