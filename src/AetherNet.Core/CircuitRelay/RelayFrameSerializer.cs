// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using AetherNet.Models;

namespace AetherNet.CircuitRelay;

/// <summary>
/// Binary serialization for <see cref="RelayFrame"/> — the cross-language wire format for
/// native circuit-relay-v2, carried in <c>MeshPacket.Payload</c>. Conventions mirror
/// <c>DtnEnvelopeSerializer</c> / <c>PacketSerializer</c> exactly so the eight language
/// SDKs stay byte-identical (and are pinned by the <c>fixtures/circuit-relay/</c> corpus):
///
/// <list type="bullet">
///   <item>every frame begins with a single format-version byte; readers reject any other value;</item>
///   <item>all multi-byte integers are little-endian;</item>
///   <item>the 16-byte <see cref="RelayFrame.ConnectionId"/> is the <see cref="Guid"/> in
///         RFC-4122 big-endian order (never the .NET mixed-endian default);</item>
///   <item>strings are uint16-LE length-prefixed UTF-8;</item>
///   <item>the payload is int32-LE length-prefixed raw bytes, and is always the last field.</item>
/// </list>
///
/// Layout (fixed, every field always present):
/// <code>
/// version u8 | type u8 | status u8
/// srcUhid u16+utf8 | dstUhid u16+utf8 | relayUhid u16+utf8
/// connId 16B(BE) | reservationExpiresAtMs i64 | limitDurationSeconds i32 | limitDataBytes i64
/// payload i32+bytes
/// </code>
/// Minimum size (all strings empty, no payload): 49 bytes.
/// </summary>
public static class RelayFrameSerializer
{
    /// <summary>Format-version byte at offset 0 of every relay frame.</summary>
    public const byte Version = 0x01;

    private const int MaxPayload = 16 * 1024 * 1024;
    private const byte MaxType = (byte)RelayMessageType.Data;   // 7
    private const byte MaxStatus = (byte)RelayStatus.MalformedMessage; // 6

    public static byte[] Serialize(RelayFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        using var ms = new MemoryStream(48 + f.Payload.Length);
        ms.WriteByte(Version);
        ms.WriteByte((byte)f.Type);
        ms.WriteByte((byte)f.Status);
        WriteStr(ms, f.SourceUhid);
        WriteStr(ms, f.DestinationUhid);
        WriteStr(ms, f.RelayUhid);
        WriteGuidBe(ms, f.ConnectionId);
        WriteI64(ms, f.ReservationExpiresAtMs);
        WriteI32(ms, f.LimitDurationSeconds);
        WriteI64(ms, f.LimitDataBytes);
        WriteBytes32(ms, f.Payload ?? []);
        return ms.ToArray();
    }

    public static RelayFrame Deserialize(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.ExpectVersion();

        var type = r.ReadByte();
        if (type == 0 || type > MaxType)
            throw new FormatException($"Relay: invalid message type {type}");

        var status = r.ReadByte();
        if (status > MaxStatus)
            throw new FormatException($"Relay: invalid status {status}");

        var src = r.ReadStr();
        var dst = r.ReadStr();
        var relay = r.ReadStr();
        var connId = r.ReadGuidBe();
        var reservationExpiresAtMs = r.ReadI64();
        var limitDurationSeconds = r.ReadI32();
        var limitDataBytes = r.ReadI64();
        var payload = r.ReadBytes32();

        return new RelayFrame
        {
            Type = (RelayMessageType)type,
            Status = (RelayStatus)status,
            SourceUhid = src,
            DestinationUhid = dst,
            RelayUhid = relay,
            ConnectionId = connId,
            ReservationExpiresAtMs = reservationExpiresAtMs,
            LimitDurationSeconds = limitDurationSeconds,
            LimitDataBytes = limitDataBytes,
            Payload = payload,
        };
    }

    // ── Low-level helpers (identical idiom to DtnEnvelopeSerializer) ─────────────

    private static void WriteGuidBe(Stream s, Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        s.Write(b);
    }

    private static void WriteI32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteStr(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str ?? string.Empty);
        if (bytes.Length > 65535)
            throw new InvalidOperationException($"Relay: string too long ({bytes.Length} bytes)");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)bytes.Length);
        s.Write(len);
        s.Write(bytes);
    }

    private static void WriteBytes32(Stream s, byte[] data)
    {
        if (data.Length > MaxPayload)
            throw new InvalidOperationException($"Relay: payload too large ({data.Length} bytes)");
        WriteI32(s, data.Length);
        s.Write(data);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public void ExpectVersion()
        {
            var v = ReadByte();
            if (v != Version)
                throw new FormatException($"Relay: unsupported frame version 0x{v:x2}");
        }

        public byte ReadByte()
        {
            var b = _data[_pos];
            _pos += 1;
            return b;
        }

        public Guid ReadGuidBe()
        {
            var g = new Guid(_data.Slice(_pos, 16), bigEndian: true);
            _pos += 16;
            return g;
        }

        public int ReadI32()
        {
            var v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public long ReadI64()
        {
            var v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public ushort ReadU16()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos, 2));
            _pos += 2;
            return v;
        }

        public string ReadStr()
        {
            int n = ReadU16();
            if (n == 0) return string.Empty;
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, n));
            _pos += n;
            return s;
        }

        public byte[] ReadBytes32()
        {
            int n = ReadI32();
            if (n < 0 || n > MaxPayload)
                throw new FormatException($"Relay: invalid payload length {n}");
            var bytes = _data.Slice(_pos, n).ToArray();
            _pos += n;
            return bytes;
        }
    }
}
