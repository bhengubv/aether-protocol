// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using AetherNet.Security.Services;

namespace AetherNet.Security.Sync;

/// <summary>
/// A signed device-membership record. A user links a new device by having their
/// long-term Ed25519 identity key sign the new device's own public key; every
/// other device verifies that signature to admit the newcomer into the "self"
/// device set — no central directory, no server. Because Ed25519 signatures are
/// deterministic, the serialized record is byte-identical across SDKs.
/// </summary>
/// <param name="DeviceId">The linked device's identifier.</param>
/// <param name="DevicePublicKey">The device's own 32-byte Ed25519 public key.</param>
/// <param name="IssuedAtMs">When the link was issued (Unix ms).</param>
/// <param name="Signature">64-byte Ed25519 signature by the user's identity key over the signed body.</param>
public sealed record DeviceLink(
    string DeviceId,
    byte[] DevicePublicKey,
    long IssuedAtMs,
    byte[] Signature);

/// <summary>Serializes, signs and verifies <see cref="DeviceLink"/> records.</summary>
public static class DeviceLinkCodec
{
    /// <summary>Wire format version; readers reject any other value.</summary>
    public const byte FormatVersion = 0x01;

    /// <summary>
    /// The canonical signed body (everything but the signature): version ·
    /// device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE).
    /// Signer and verifier operate over exactly these bytes.
    /// </summary>
    public static byte[] SignedBody(string deviceId, byte[] devicePublicKey, long issuedAtMs)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(devicePublicKey);
        if (devicePublicKey.Length != 32)
            throw new ArgumentException("Device public key must be 32 bytes.", nameof(devicePublicKey));

        var id = Encoding.UTF8.GetBytes(deviceId);
        if (id.Length > ushort.MaxValue) throw new ArgumentException("DeviceId is too long.", nameof(deviceId));

        var body = new byte[1 + 2 + id.Length + 32 + 8];
        var span = body.AsSpan();
        var o = 0;
        span[o++] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)id.Length); o += 2;
        id.CopyTo(span.Slice(o)); o += id.Length;
        devicePublicKey.CopyTo(span.Slice(o)); o += 32;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o, 8), issuedAtMs);
        return body;
    }

    /// <summary>Creates a device-link signed by the user's 32-byte Ed25519 identity private key.</summary>
    public static DeviceLink Create(string deviceId, byte[] devicePublicKey, long issuedAtMs, byte[] identityPrivateKey)
    {
        var body = SignedBody(deviceId, devicePublicKey, issuedAtMs);
        var signature = Ed25519SigningService.Sign(identityPrivateKey, body);
        return new DeviceLink(deviceId, devicePublicKey, issuedAtMs, signature);
    }

    /// <summary>
    /// True if <paramref name="link"/> was signed by the identity behind
    /// <paramref name="identityPublicKey"/> — i.e. this device belongs to that user.
    /// </summary>
    public static bool Verify(DeviceLink link, byte[] identityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(identityPublicKey);
        if (link.Signature is not { Length: 64 }) return false;
        if (link.DevicePublicKey is not { Length: 32 }) return false;

        var body = SignedBody(link.DeviceId, link.DevicePublicKey, link.IssuedAtMs);
        return Ed25519SigningService.Verify(identityPublicKey, body, link.Signature);
    }

    /// <summary>Serializes a link as its signed body followed by the 64-byte signature.</summary>
    public static byte[] Serialize(DeviceLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Signature is not { Length: 64 })
            throw new ArgumentException("Signature must be 64 bytes.", nameof(link));

        var body = SignedBody(link.DeviceId, link.DevicePublicKey, link.IssuedAtMs);
        var buffer = new byte[body.Length + 64];
        body.CopyTo(buffer, 0);
        link.Signature.CopyTo(buffer, body.Length);
        return buffer;
    }

    /// <summary>Parses a serialized link, validating framing.</summary>
    public static DeviceLink Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var span = data.AsSpan();
        var o = 0;

        if (span.Length < 1 + 2 + 32 + 8 + 64) throw new FormatException("DeviceLink is too short.");
        if (span[o++] != FormatVersion) throw new FormatException("Unsupported DeviceLink format version.");

        var idLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(o, 2)); o += 2;
        if (o + idLen + 32 + 8 + 64 > span.Length) throw new FormatException("DeviceLink is truncated.");
        var deviceId = Encoding.UTF8.GetString(span.Slice(o, idLen)); o += idLen;
        var devicePublicKey = span.Slice(o, 32).ToArray(); o += 32;
        var issuedAtMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
        var signature = span.Slice(o, 64).ToArray();

        return new DeviceLink(deviceId, devicePublicKey, issuedAtMs, signature);
    }
}
