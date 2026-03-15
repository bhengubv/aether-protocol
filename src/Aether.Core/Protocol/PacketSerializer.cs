// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace Aether.Protocol;

/// <summary>
/// Binary serializer/deserializer for <see cref="MeshPacket"/>.
///
/// Wire format (all multi-byte integers are little-endian):
///
///   [1 byte]  Protocol version
///   [1 byte]  Packet type
///   [16 bytes] Packet ID (GUID)
///   [1 byte]  Priority
///   [4 bytes] TTL (int32)
///   [8 bytes] TimestampMs (int64)
///   [2 bytes] SourceUhid length (uint16)
///   [N bytes] SourceUhid (UTF-8)
///   [2 bytes] DestinationUhid length (uint16)
///   [N bytes] DestinationUhid (UTF-8)
///   [2 bytes] PacketNonce length (uint16)
///   [N bytes] PacketNonce
///   [4 bytes] Payload length (int32)
///   [N bytes] Payload
///   [2 bytes] Signature length (uint16)
///   [N bytes] Signature
/// </summary>
public static class PacketSerializer
{
    /// <summary>
    /// Serializes a <see cref="MeshPacket"/> to its binary wire format.
    /// </summary>
    public static byte[] Serialize(MeshPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var sourceBytes = Encoding.UTF8.GetBytes(packet.SourceUhid);
        var destBytes = Encoding.UTF8.GetBytes(packet.DestinationUhid);

        // Calculate total size
        var totalSize =
            1  // protocol version
          + 1  // packet type
          + 16 // guid
          + 1  // priority
          + 4  // ttl
          + 8  // timestamp
          + 2 + sourceBytes.Length
          + 2 + destBytes.Length
          + 2 + packet.PacketNonce.Length
          + 4 + packet.Payload.Length
          + 2 + packet.Signature.Length;

        var buffer = new byte[totalSize];
        var offset = 0;

        // Protocol version
        buffer[offset++] = packet.ProtocolVersion;

        // Packet type
        buffer[offset++] = (byte)packet.Type;

        // Packet ID
        if (!packet.Id.TryWriteBytes(buffer.AsSpan(offset)))
            packet.Id.ToByteArray().CopyTo(buffer, offset);
        offset += 16;

        // Priority
        buffer[offset++] = packet.Priority;

        // TTL
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), packet.Ttl);
        offset += 4;

        // TimestampMs
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), packet.TimestampMs);
        offset += 8;

        // SourceUhid
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)sourceBytes.Length);
        offset += 2;
        sourceBytes.CopyTo(buffer, offset);
        offset += sourceBytes.Length;

        // DestinationUhid
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)destBytes.Length);
        offset += 2;
        destBytes.CopyTo(buffer, offset);
        offset += destBytes.Length;

        // PacketNonce
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)packet.PacketNonce.Length);
        offset += 2;
        packet.PacketNonce.CopyTo(buffer, offset);
        offset += packet.PacketNonce.Length;

        // Payload
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), packet.Payload.Length);
        offset += 4;
        packet.Payload.CopyTo(buffer, offset);
        offset += packet.Payload.Length;

        // Signature
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)packet.Signature.Length);
        offset += 2;
        packet.Signature.CopyTo(buffer, offset);

        return buffer;
    }

    /// <summary>
    /// Deserializes a <see cref="MeshPacket"/> from its binary wire format.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the data is too short or malformed.</exception>
    public static MeshPacket Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Deserialize(data.AsSpan());
    }

    /// <summary>
    /// Deserializes a <see cref="MeshPacket"/> from a span of bytes.
    /// </summary>
    public static MeshPacket Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 31) // minimum: version(1) + type(1) + guid(16) + priority(1) + ttl(4) + ts(8) + lengths(6*0 + 2+2+2+4+2=12) = 43 minimum with 0-length strings
            throw new ArgumentException("Data is too short to contain a valid MeshPacket.", nameof(data));

        var offset = 0;
        var packet = new MeshPacket();

        // Protocol version
        packet.ProtocolVersion = data[offset++];

        // Packet type
        packet.Type = (PacketType)data[offset++];

        // Packet ID
        packet.Id = new Guid(data.Slice(offset, 16));
        offset += 16;

        // Priority
        packet.Priority = data[offset++];

        // TTL
        packet.Ttl = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;

        // TimestampMs
        packet.TimestampMs = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset));
        offset += 8;

        // SourceUhid
        var sourceLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        EnsureRemaining(data, offset, sourceLen);
        packet.SourceUhid = Encoding.UTF8.GetString(data.Slice(offset, sourceLen));
        offset += sourceLen;

        // DestinationUhid
        var destLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        EnsureRemaining(data, offset, destLen);
        packet.DestinationUhid = Encoding.UTF8.GetString(data.Slice(offset, destLen));
        offset += destLen;

        // PacketNonce
        var nonceLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        EnsureRemaining(data, offset, nonceLen);
        packet.PacketNonce = data.Slice(offset, nonceLen).ToArray();
        offset += nonceLen;

        // Payload
        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;
        if (payloadLen < 0)
            throw new ArgumentException("Negative payload length.", nameof(data));
        EnsureRemaining(data, offset, payloadLen);
        packet.Payload = data.Slice(offset, payloadLen).ToArray();
        offset += payloadLen;

        // Signature
        var sigLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        EnsureRemaining(data, offset, sigLen);
        packet.Signature = data.Slice(offset, sigLen).ToArray();

        // Reconstruct CreatedAt from TimestampMs
        packet.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(packet.TimestampMs).UtcDateTime;

        return packet;
    }

    /// <summary>
    /// Attempts to deserialize a packet, returning false on failure instead of throwing.
    /// </summary>
    public static bool TryDeserialize(byte[] data, out MeshPacket? packet)
    {
        try
        {
            packet = Deserialize(data);
            return true;
        }
        catch
        {
            packet = null;
            return false;
        }
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int required)
    {
        if (offset + required > data.Length)
            throw new ArgumentException(
                $"Insufficient data: need {required} bytes at offset {offset}, but only {data.Length - offset} remain.",
                nameof(data));
    }
}
