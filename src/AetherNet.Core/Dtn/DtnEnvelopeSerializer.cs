// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Binary DTN-envelope serialization — the cross-language wire format for the
/// three DTN packet bodies (bundle / custody-ack / delivery-receipt) carried in
/// <c>MeshPacket.Payload</c>. Conventions mirror <c>PacketSerializer</c>: all
/// multi-byte integers little-endian; the 16-byte bundle id is the <see cref="Guid"/>
/// in RFC-4122 big-endian order (never the .NET mixed-endian default); strings are
/// uint16-LE length-prefixed UTF-8; the encrypted payload is int32-LE length-prefixed
/// raw bytes. Every envelope begins with a single format-version byte so the format
/// can evolve without a flag-day — a reader rejects any unknown version.
///
/// Cleartext routing fields are laid out first and the opaque encrypted payload last,
/// so a later version can encrypt sender/recipient with no field-shuffle.
/// </summary>
public static class DtnEnvelopeSerializer
{
    /// <summary>Format-version byte at offset 0 of every DTN envelope.</summary>
    public const byte Version = 0x01;

    private const int MaxEnvelopePayload = 16 * 1024 * 1024;

    // ── Bundle ────────────────────────────────────────────────────────────────

    public static byte[] SerializeBundle(DtnBundle b)
    {
        ArgumentNullException.ThrowIfNull(b);
        using var ms = new MemoryStream(64 + (b.EncryptedPayload?.Length ?? 0));
        ms.WriteByte(Version);
        WriteGuidBe(ms, b.Id);
        ms.WriteByte((byte)b.Priority);
        ms.WriteByte((byte)b.Status);
        WriteI32(ms, b.CopyCount);
        WriteI32(ms, b.MaxCopies);
        WriteI32(ms, b.HopCount);
        WriteI64(ms, ToUnixMs(b.CreatedAt));
        WriteI64(ms, ToUnixMs(b.ExpiresAt));
        WriteStr(ms, b.SenderUhid);
        WriteStr(ms, b.RecipientUhid);
        WriteStr(ms, b.SenderGeohash ?? string.Empty);
        WriteStr(ms, b.RecipientLastGeohash ?? string.Empty);
        WriteBytes32(ms, b.EncryptedPayload ?? Array.Empty<byte>());
        return ms.ToArray();
    }

    public static DtnBundle DeserializeBundle(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.ExpectVersion();
        var id = r.ReadGuidBe();
        var priority = r.ReadByte();
        if (priority > (byte)BundlePriority.Sos)
            throw new FormatException($"DTN: invalid priority {priority}");
        var status = r.ReadByte();
        if (status > (byte)BundleStatus.Failed)
            throw new FormatException($"DTN: invalid status {status}");
        var copyCount = r.ReadI32();
        var maxCopies = r.ReadI32();
        var hopCount = r.ReadI32();
        var createdAt = FromUnixMs(r.ReadI64());
        var expiresAt = FromUnixMs(r.ReadI64());
        var senderUhid = r.ReadStr();
        var recipientUhid = r.ReadStr();
        var senderGeohash = r.ReadStr();
        var recipientLastGeohash = r.ReadStr();
        var payload = r.ReadBytes32();
        return new DtnBundle
        {
            Id = id,
            SenderUhid = senderUhid,
            RecipientUhid = recipientUhid,
            EncryptedPayload = payload,
            Priority = (BundlePriority)priority,
            Status = (BundleStatus)status,
            CopyCount = copyCount,
            MaxCopies = maxCopies,
            SenderGeohash = senderGeohash,
            RecipientLastGeohash = recipientLastGeohash,
            HopCount = hopCount,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    // ── Custody-ack ───────────────────────────────────────────────────────────

    public static byte[] SerializeCustodyAck(Guid bundleId, bool accepted)
    {
        var buf = new byte[18];
        buf[0] = Version;
        bundleId.TryWriteBytes(buf.AsSpan(1, 16), bigEndian: true, out _);
        buf[17] = accepted ? (byte)0x01 : (byte)0x00;
        return buf;
    }

    public static (Guid BundleId, bool Accepted) DeserializeCustodyAck(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.ExpectVersion();
        var id = r.ReadGuidBe();
        var accepted = r.ReadByte() != 0;
        return (id, accepted);
    }

    // ── Delivery-receipt ──────────────────────────────────────────────────────

    public static byte[] SerializeDeliveryReceipt(DtnDeliveryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var ms = new MemoryStream(48);
        ms.WriteByte(Version);
        WriteGuidBe(ms, receipt.BundleId);
        WriteStr(ms, receipt.RecipientUhid);
        WriteI32(ms, receipt.TotalHops);
        WriteI32(ms, receipt.TotalCustodyTransfers);
        WriteI64(ms, ToUnixMs(receipt.DeliveredAt));
        return ms.ToArray();
    }

    public static DtnDeliveryReceipt DeserializeDeliveryReceipt(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.ExpectVersion();
        var bundleId = r.ReadGuidBe();
        var recipientUhid = r.ReadStr();
        var totalHops = r.ReadI32();
        var totalCustodyTransfers = r.ReadI32();
        var deliveredAt = FromUnixMs(r.ReadI64());
        return new DtnDeliveryReceipt
        {
            BundleId = bundleId,
            RecipientUhid = recipientUhid,
            TotalHops = totalHops,
            TotalCustodyTransfers = totalCustodyTransfers,
            DeliveredAt = deliveredAt,
        };
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private static long ToUnixMs(DateTime dt)
    {
        // Treat Unspecified as UTC (the model populates DateTime.UtcNow and
        // deserialized values are reconstructed as UTC).
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private static DateTime FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

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
            throw new InvalidOperationException($"DTN: string too long ({bytes.Length} bytes)");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)bytes.Length);
        s.Write(len);
        s.Write(bytes);
    }

    private static void WriteBytes32(Stream s, byte[] data)
    {
        if (data.Length > MaxEnvelopePayload)
            throw new InvalidOperationException($"DTN: payload too large ({data.Length} bytes)");
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
                throw new FormatException($"DTN: unsupported envelope version 0x{v:x2}");
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
            if (n < 0 || n > MaxEnvelopePayload)
                throw new FormatException($"DTN: invalid payload length {n}");
            var bytes = _data.Slice(_pos, n).ToArray();
            _pos += n;
            return bytes;
        }
    }
}
