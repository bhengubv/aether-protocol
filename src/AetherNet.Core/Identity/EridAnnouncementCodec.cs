// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace AetherNet.Identity;

/// <summary>
/// Frames the in-session ERID announcement — the message a node sends a peer INSIDE an established
/// Signal session to share its secret <c>routingKey</c> (plus the rotation parameters it uses), so
/// the peer can resolve its rotating wire address via <see cref="EridDirectory"/>.
///
/// <para>The bytes are carried <em>encrypted</em> by the Signal session, so this is framing only —
/// no encryption of its own. A 4-byte magic sentinel + version lets a receiver tell an ERID
/// announcement apart from other in-session application data before trying to parse it. Integer
/// fields are big-endian so every language port frames byte-identically.</para>
/// </summary>
public static class EridAnnouncementCodec
{
    // 'A' 'E' 'R' 'D' — "AetherNet ERID Directory announcement". Distinct from any other in-session magic.
    private static readonly byte[] Magic = { 0x41, 0x45, 0x52, 0x44 };
    private const byte Version = 1;

    // magic(4) + version(1) + epochSeconds(4) + eridLength(4) + routingKeyLen(4) = 17-byte header.
    private const int HeaderLength = 17;

    /// <summary>Frame an announcement carrying <paramref name="routingKey"/> and the rotation params.</summary>
    public static byte[] Encode(
        byte[] routingKey,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds,
        int eridLength = EphemeralRoutingId.DefaultLength)
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        if (routingKey.Length == 0)
            throw new ArgumentException("routingKey cannot be empty.", nameof(routingKey));
        if (epochSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochSeconds));
        if (eridLength is < 1 or > 51)
            throw new ArgumentOutOfRangeException(nameof(eridLength));

        var buf = new byte[HeaderLength + routingKey.Length];
        var span = buf.AsSpan();
        Magic.CopyTo(span);
        span[4] = Version;
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(5), epochSeconds);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(9), eridLength);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(13), routingKey.Length);
        routingKey.CopyTo(span.Slice(HeaderLength));
        return buf;
    }

    /// <summary>
    /// Parse an announcement. Returns false (not throwing) when the bytes are not a well-formed
    /// ERID announcement — so a receiver can cheaply test an arbitrary decrypted in-session payload
    /// against the magic without it being an error.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out byte[] routingKey, out int epochSeconds, out int eridLength)
    {
        routingKey = Array.Empty<byte>();
        epochSeconds = 0;
        eridLength = 0;

        if (data.Length < HeaderLength) return false;
        if (!data.Slice(0, 4).SequenceEqual(Magic)) return false;
        if (data[4] != Version) return false;

        epochSeconds = BinaryPrimitives.ReadInt32BigEndian(data.Slice(5));
        eridLength = BinaryPrimitives.ReadInt32BigEndian(data.Slice(9));
        var keyLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(13));

        if (epochSeconds <= 0) return false;
        if (eridLength is < 1 or > 51) return false;
        if (keyLen <= 0 || HeaderLength + (long)keyLen > data.Length) return false;

        routingKey = data.Slice(HeaderLength, keyLen).ToArray();
        return true;
    }
}
