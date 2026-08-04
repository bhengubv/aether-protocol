// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text;
using AetherNet.Cartography.Models;

namespace AetherNet.Cartography;

/// <summary>
/// Canonical byte layout that BOTH the witness and the subject sign with their real Ed25519 identity keys
/// for a <see cref="PoLWitnessAttestation"/> — the Proof-of-Location analogue of <c>PoVTokenCodec</c>,
/// extended to bind location, place, and a quantized time bucket so independent witnesses in one
/// encounter sign identical bytes and their signatures aggregate.
///
/// Wire layout (little-endian, i32 length prefixes — same discipline as <c>PoVTokenCodec</c>; must stay
/// byte-identical across every language SDK):
/// <code>
///   SubjectLen(4 LE) || Subject(UTF-8)
///   GeohashLen(4 LE) || Geohash(UTF-8)      // coarse, precision 7 (~150 m)
///   PlaceIdLen(4 LE) || PlaceId(UTF-8)      // "" for free-roam
///   TimeBucket(8 LE i64)                    // floor(unixMs / TimeBucketMs)
///   Transport(1)                            // Ble=0 / Nfc=1 / NearLink=2
/// </code>
/// </summary>
public static class PoLAttestationCodec
{
    /// <summary>Time quantization for attestations — 5 minutes, so co-present witnesses agree on the bucket.</summary>
    public const long TimeBucketMs = 300_000;

    /// <summary>The signed time bucket for a wall-clock unix-ms timestamp.</summary>
    public static long TimeBucketFor(long unixMs) => unixMs / TimeBucketMs;

    /// <summary>Build the canonical signable bytes for a witness attestation body.</summary>
    public static byte[] BuildSignableData(string subjectUhid, string geohash, string placeId, long timeBucket, PoLTransport transport)
    {
        var subject = Encoding.UTF8.GetBytes(subjectUhid ?? string.Empty);
        var geo = Encoding.UTF8.GetBytes(geohash ?? string.Empty);
        var place = Encoding.UTF8.GetBytes(placeId ?? string.Empty);

        var data = new byte[4 + subject.Length + 4 + geo.Length + 4 + place.Length + 8 + 1];
        int o = 0;

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o, 4), subject.Length); o += 4;
        subject.CopyTo(data.AsSpan(o)); o += subject.Length;

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o, 4), geo.Length); o += 4;
        geo.CopyTo(data.AsSpan(o)); o += geo.Length;

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o, 4), place.Length); o += 4;
        place.CopyTo(data.AsSpan(o)); o += place.Length;

        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(o, 8), timeBucket); o += 8;
        data[o] = (byte)transport;

        return data;
    }

    /// <summary>Convenience overload taking the attestation directly.</summary>
    public static byte[] BuildSignableData(PoLWitnessAttestation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return BuildSignableData(a.SubjectUhid, a.Geohash, a.PlaceId, a.TimeBucket, a.Transport);
    }
}
