// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace AetherNet.BitTorrent.Utp;

/// <summary>Thrown when a µTP packet is malformed.</summary>
public sealed class UtpException : Exception
{
    public UtpException(string message) : base(message) { }
}

/// <summary>µTP packet types (BEP-29).</summary>
public enum UtpPacketType : byte
{
    Data = 0,
    Fin = 1,
    State = 2,
    Reset = 3,
    Syn = 4,
}

/// <summary>
/// A µTP packet (BEP-29, version 1). The fixed 20-byte header is:
/// <c>type|version(1) · extension(1) · connection_id(2) · timestamp_us(4) · timestamp_diff_us(4) ·
/// wnd_size(4) · seq_nr(2) · ack_nr(2)</c>, all big-endian, followed by any extensions and the payload.
/// </summary>
public sealed class UtpPacket
{
    public const byte Version = 1;
    public const int HeaderSize = 20;

    public UtpPacketType Type { get; init; }
    public ushort ConnectionId { get; init; }
    public uint TimestampMicros { get; init; }
    public uint TimestampDiffMicros { get; init; }
    public uint WindowSize { get; init; }
    public ushort SeqNr { get; init; }
    public ushort AckNr { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public byte[] ToBytes()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        buf[0] = (byte)(((byte)Type << 4) | Version);
        buf[1] = 0; // no extensions
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), ConnectionId);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), TimestampMicros);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), TimestampDiffMicros);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), WindowSize);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16), SeqNr);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(18), AckNr);
        Payload.CopyTo(buf, HeaderSize);
        return buf;
    }

    public static UtpPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new UtpException($"µTP packet is {data.Length} bytes, shorter than the {HeaderSize}-byte header");

        byte typeAndVersion = data[0];
        byte version = (byte)(typeAndVersion & 0x0F);
        if (version != Version) throw new UtpException($"unsupported µTP version {version}");
        var type = (UtpPacketType)(typeAndVersion >> 4);

        // Walk the extension chain (each: next_ext(1) len(1) data(len)) to locate the payload.
        int offset = HeaderSize;
        int nextExtension = data[1];
        while (nextExtension != 0)
        {
            if (offset + 2 > data.Length) throw new UtpException("truncated µTP extension header");
            int thisNext = data[offset];
            int len = data[offset + 1];
            offset += 2 + len;
            if (offset > data.Length) throw new UtpException("truncated µTP extension data");
            nextExtension = thisNext;
        }

        return new UtpPacket
        {
            Type = type,
            ConnectionId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2)),
            TimestampMicros = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            TimestampDiffMicros = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
            WindowSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4)),
            SeqNr = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(16, 2)),
            AckNr = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(18, 2)),
            Payload = data[offset..].ToArray(),
        };
    }
}
