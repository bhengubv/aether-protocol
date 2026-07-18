// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace AetherNet.BitTorrent.PeerWire;

/// <summary>The BEP-3 peer-wire message ids.</summary>
public enum PeerMessageType : byte
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    Port = 9,
}

/// <summary>
/// A BitTorrent peer-wire message (BEP-3). On the wire: a 4-byte big-endian length prefix, then —
/// unless the length is 0 (the keep-alive) — a 1-byte id followed by its payload.
///
/// <para>The raw <see cref="Payload"/> is authoritative; known types expose decoded accessors.
/// Unknown ids (e.g. the extension-protocol id 20 added with BEP-10) still parse — carrying
/// <see cref="Id"/> + <see cref="Payload"/> without decoding — so a real peer's traffic never crashes
/// the parser.</para>
/// </summary>
public sealed class PeerMessage
{
    /// <summary>The message id, or null for a keep-alive.</summary>
    public byte? Id { get; }

    /// <summary>Raw payload — everything after the id (empty for a keep-alive or a payload-less message).</summary>
    public byte[] Payload { get; }

    public bool IsKeepAlive => Id is null;

    /// <summary>The known message type, or null for a keep-alive or an unknown/extension id.</summary>
    public PeerMessageType? Type => Id is { } id && Enum.IsDefined((PeerMessageType)id) ? (PeerMessageType)id : null;

    private PeerMessage(byte? id, byte[] payload)
    {
        Id = id;
        Payload = payload;
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    public static readonly PeerMessage KeepAlive = new(null, Array.Empty<byte>());
    public static PeerMessage Choke() => new((byte)PeerMessageType.Choke, Array.Empty<byte>());
    public static PeerMessage Unchoke() => new((byte)PeerMessageType.Unchoke, Array.Empty<byte>());
    public static PeerMessage Interested() => new((byte)PeerMessageType.Interested, Array.Empty<byte>());
    public static PeerMessage NotInterested() => new((byte)PeerMessageType.NotInterested, Array.Empty<byte>());

    public static PeerMessage Have(int pieceIndex)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(p, pieceIndex);
        return new((byte)PeerMessageType.Have, p);
    }

    public static PeerMessage Bitfield(byte[] bitfield) =>
        new((byte)PeerMessageType.Bitfield, (byte[])(bitfield ?? throw new ArgumentNullException(nameof(bitfield))).Clone());

    public static PeerMessage Request(int index, int begin, int length) => BlockRef(PeerMessageType.Request, index, begin, length);
    public static PeerMessage Cancel(int index, int begin, int length) => BlockRef(PeerMessageType.Cancel, index, begin, length);

    private static PeerMessage BlockRef(PeerMessageType type, int index, int begin, int length)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), index);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), begin);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), length);
        return new((byte)type, p);
    }

    public static PeerMessage Piece(int index, int begin, byte[] block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var p = new byte[8 + block.Length];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), index);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), begin);
        block.CopyTo(p, 8);
        return new((byte)PeerMessageType.Piece, p);
    }

    public static PeerMessage Port(int port)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var p = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)port);
        return new((byte)PeerMessageType.Port, p);
    }

    /// <summary>An unknown / extension-protocol message (e.g. BEP-10 id 20).</summary>
    public static PeerMessage Unknown(byte id, byte[] payload) =>
        new(id, (byte[])(payload ?? throw new ArgumentNullException(nameof(payload))).Clone());

    // ── Decoded accessors ───────────────────────────────────────────────────────

    public int GetHavePieceIndex()
    {
        Require(PeerMessageType.Have);
        return BinaryPrimitives.ReadInt32BigEndian(Payload);
    }

    public (int Index, int Begin, int Length) GetBlockRef()
    {
        if (Type is not (PeerMessageType.Request or PeerMessageType.Cancel))
            throw new PeerWireException($"{Describe()} is not a request/cancel");
        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0)),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8)));
    }

    public (int Index, int Begin, byte[] Block) GetPiece()
    {
        Require(PeerMessageType.Piece);
        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0)),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)),
            Payload[8..]);
    }

    public int GetPort()
    {
        Require(PeerMessageType.Port);
        return BinaryPrimitives.ReadUInt16BigEndian(Payload);
    }

    public byte[] GetBitfield()
    {
        Require(PeerMessageType.Bitfield);
        return (byte[])Payload.Clone();
    }

    // ── Wire framing ────────────────────────────────────────────────────────────

    /// <summary>Serialise to the wire: 4-byte big-endian length prefix + id + payload (keep-alive = four zero bytes).</summary>
    public byte[] ToBytes()
    {
        if (IsKeepAlive) return new byte[4]; // length 0
        int len = 1 + Payload.Length;
        var buf = new byte[4 + len];
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0), len);
        buf[4] = Id!.Value;
        Payload.CopyTo(buf, 5);
        return buf;
    }

    /// <summary>Parse a message body (the <c>length</c> bytes AFTER the 4-byte prefix; empty = keep-alive).</summary>
    public static PeerMessage ParseBody(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return KeepAlive;
        byte id = body[0];
        var payload = body[1..].ToArray();

        // Validate declared payload sizes of the known fixed-shape messages.
        switch (id)
        {
            case (byte)PeerMessageType.Choke:
            case (byte)PeerMessageType.Unchoke:
            case (byte)PeerMessageType.Interested:
            case (byte)PeerMessageType.NotInterested:
                if (payload.Length != 0) throw new PeerWireException($"message id {id} must have an empty payload");
                break;
            case (byte)PeerMessageType.Have:
                if (payload.Length != 4) throw new PeerWireException("'have' payload must be 4 bytes");
                break;
            case (byte)PeerMessageType.Request:
            case (byte)PeerMessageType.Cancel:
                if (payload.Length != 12) throw new PeerWireException($"message id {id} payload must be 12 bytes");
                break;
            case (byte)PeerMessageType.Piece:
                if (payload.Length < 8) throw new PeerWireException("'piece' payload must be at least 8 bytes");
                break;
            case (byte)PeerMessageType.Port:
                if (payload.Length != 2) throw new PeerWireException("'port' payload must be 2 bytes");
                break;
            // Bitfield (5): any length. Unknown/extension ids: carried raw.
        }
        return new PeerMessage(id, payload);
    }

    /// <summary>Parse a whole frame including the 4-byte length prefix.</summary>
    public static PeerMessage ParseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) throw new PeerWireException("frame too short for a length prefix");
        int len = BinaryPrimitives.ReadInt32BigEndian(frame);
        if (len < 0) throw new PeerWireException("negative message length");
        if (frame.Length < 4 + len) throw new PeerWireException($"frame declares {len} bytes but only {frame.Length - 4} follow");
        return ParseBody(frame.Slice(4, len));
    }

    private void Require(PeerMessageType type)
    {
        if (Type != type) throw new PeerWireException($"{Describe()} is not a {type} message");
    }

    private string Describe() => IsKeepAlive ? "keep-alive" : Type?.ToString() ?? $"message id {Id}";
}
