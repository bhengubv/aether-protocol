// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text;
using AetherNet.Map.Crdt;

namespace AetherNet.Map.Wire;

/// <summary>
/// Wire codec for a MapFeatureRequest (packet 45): "send me the features in geohash cell G that changed
/// since HLC H" — the anti-entropy pull a node issues on entering a new neighbourhood.
/// Layout: <c>version(u8) · geohash(u16 len + UTF-8) · since(i64 ms LE · u16 counter LE · node u16 len + UTF-8)</c>.
/// </summary>
public static class MapFeatureRequestCodec
{
    public const byte Version = 1;

    public static byte[] Serialize(string geohash, HybridLogicalClock since)
    {
        ArgumentNullException.ThrowIfNull(geohash);
        var geo = Encoding.UTF8.GetBytes(geohash);
        var node = Encoding.UTF8.GetBytes(since.NodeId ?? string.Empty);
        if (geo.Length > ushort.MaxValue || node.Length > ushort.MaxValue)
            throw new FormatException("Field exceeds u16 length.");

        var buf = new byte[1 + 2 + geo.Length + 8 + 2 + 2 + node.Length];
        int o = 0;
        buf[o++] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)geo.Length); o += 2;
        geo.CopyTo(buf.AsSpan(o)); o += geo.Length;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(o, 8), since.PhysicalMs); o += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), since.Counter); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o, 2), (ushort)node.Length); o += 2;
        node.CopyTo(buf.AsSpan(o));
        return buf;
    }

    public static (string Geohash, HybridLogicalClock Since) Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int o = 0;
        void Need(int n) { if (o + n > bytes.Length) throw new FormatException("Truncated MapFeatureRequest."); }

        Need(1);
        if (bytes[o++] != Version) throw new FormatException("Unsupported MapFeatureRequest version.");

        Need(2);
        int geoLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o, 2)); o += 2;
        Need(geoLen);
        string geohash = Encoding.UTF8.GetString(bytes, o, geoLen); o += geoLen;

        Need(8);
        long ms = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(o, 8)); o += 8;
        Need(2);
        ushort counter = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o, 2)); o += 2;
        Need(2);
        int nodeLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o, 2)); o += 2;
        Need(nodeLen);
        string node = Encoding.UTF8.GetString(bytes, o, nodeLen);

        return (geohash, new HybridLogicalClock(ms, counter, node));
    }
}
