// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace AetherNet.Security.Sync;

/// <summary>
/// Binary wire format for a <see cref="SyncRecord"/> — the unit a device gossips
/// to a user's other devices. Little-endian integers, RFC-4122 big-endian record
/// id, u16-length-prefixed UTF-8 strings, i32-length-prefixed payload — identical
/// bytes across every AetherNet SDK (verified against fixtures/sync/vectors.json).
///
/// Layout: version(u8=1) · record_id(16, big-endian) · op(u8) · logical_clock(i64 LE)
/// · created_at_ms(i64 LE) · device_id(u16 len + utf8) · item_id(u16 len + utf8)
/// · encrypted_payload(i32 len + bytes).
/// </summary>
public static class SyncRecordSerializer
{
    /// <summary>Wire format version; readers reject any other value.</summary>
    public const byte FormatVersion = 0x01;

    /// <summary>Serializes a record to its canonical bytes.</summary>
    public static byte[] Serialize(SyncRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var device = Encoding.UTF8.GetBytes(record.DeviceId ?? string.Empty);
        var item = Encoding.UTF8.GetBytes(record.ItemId ?? string.Empty);
        var payload = record.EncryptedPayload ?? Array.Empty<byte>();
        if (device.Length > ushort.MaxValue) throw new ArgumentException("DeviceId is too long.", nameof(record));
        if (item.Length > ushort.MaxValue) throw new ArgumentException("ItemId is too long.", nameof(record));

        var buffer = new byte[1 + 16 + 1 + 8 + 8 + 2 + device.Length + 2 + item.Length + 4 + payload.Length];
        var span = buffer.AsSpan();
        var o = 0;

        span[o++] = FormatVersion;
        record.RecordId.TryWriteBytes(span.Slice(o, 16), bigEndian: true, out _); o += 16;
        span[o++] = (byte)record.Op;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o, 8), record.LogicalClock); o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o, 8), record.CreatedAtMs); o += 8;
        o = WriteString(span, o, device);
        o = WriteString(span, o, item);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), payload.Length); o += 4;
        payload.CopyTo(span.Slice(o));

        return buffer;
    }

    /// <summary>Parses canonical bytes back into a record, validating framing.</summary>
    public static SyncRecord Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var span = data.AsSpan();
        var o = 0;

        if (span.Length < 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4)
            throw new FormatException("SyncRecord is too short.");
        if (span[o++] != FormatVersion)
            throw new FormatException("Unsupported SyncRecord format version.");

        var recordId = new Guid(span.Slice(o, 16), bigEndian: true); o += 16;
        var opByte = span[o++];
        if (opByte > (byte)SyncOp.Read) throw new FormatException("Unknown SyncRecord op.");
        var op = (SyncOp)opByte;
        var logicalClock = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
        var createdAtMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
        var deviceId = ReadString(span, ref o);
        var itemId = ReadString(span, ref o);

        if (o + 4 > span.Length) throw new FormatException("SyncRecord payload length is truncated.");
        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;
        if (payloadLen < 0 || o + payloadLen > span.Length)
            throw new FormatException("SyncRecord payload length is invalid.");
        var payload = span.Slice(o, payloadLen).ToArray();

        return new SyncRecord(recordId, deviceId, op, itemId, logicalClock, createdAtMs, payload);
    }

    private static int WriteString(Span<byte> span, int o, byte[] utf8)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)utf8.Length); o += 2;
        utf8.CopyTo(span.Slice(o)); o += utf8.Length;
        return o;
    }

    private static string ReadString(ReadOnlySpan<byte> span, ref int o)
    {
        if (o + 2 > span.Length) throw new FormatException("SyncRecord string length is truncated.");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(o, 2)); o += 2;
        if (o + len > span.Length) throw new FormatException("SyncRecord string is truncated.");
        var s = Encoding.UTF8.GetString(span.Slice(o, len)); o += len;
        return s;
    }
}
