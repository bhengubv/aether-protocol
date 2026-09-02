// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using AetherNet.Security.Services;

namespace AetherNet.Security.Sync;

/// <summary>
/// A signed device-REVOCATION record — the inverse of <see cref="DeviceLink"/>.
///
/// <para>
/// <see cref="DeviceLink"/> admits a device into the "self" device set by having the user's long-term
/// identity key sign the device's public key. This ejects one: when a device is lost, seized, or
/// retired, the same identity key signs a statement that its public key is no longer part of the set.
/// Every other device and contact verifies the signature and drops the revoked key (and re-keys any
/// session held with it) — no central directory, no server. Because Ed25519 signatures are
/// deterministic, the serialized record is byte-identical across SDKs.
/// </para>
///
/// <para>
/// This is the remote half of "this device is gone". <see cref="AetherNet.Security.Privacy.PanicWipe"/>
/// erases the <b>local</b> device under duress; a <see cref="DeviceRevocation"/>, gossiped to the device
/// set and contacts, invalidates a device you <b>no longer hold</b>. The two pair: local erasure plus
/// remote invalidation.
/// </para>
/// </summary>
/// <param name="DeviceId">The revoked device's identifier.</param>
/// <param name="DevicePublicKey">The revoked device's 32-byte Ed25519 public key.</param>
/// <param name="RevokedAtMs">When the revocation was issued (Unix ms). Later revocations win.</param>
/// <param name="Reason">Advisory free-text (e.g. "lost", "stolen", "retired"). Signed, so it cannot be altered.</param>
/// <param name="Signature">64-byte Ed25519 signature by the user's identity key over the signed body.</param>
public sealed record DeviceRevocation(
    string DeviceId,
    byte[] DevicePublicKey,
    long RevokedAtMs,
    string Reason,
    byte[] Signature);

/// <summary>Serializes, signs and verifies <see cref="DeviceRevocation"/> records.</summary>
public static class DeviceRevocationCodec
{
    /// <summary>Wire format version; readers reject any other value. Distinct namespace from DeviceLink
    /// by the domain tag below, so a link signature can never be replayed as a revocation.</summary>
    public const byte FormatVersion = 0x01;

    // Domain-separation tag mixed into the signed body so a DeviceLink signature (which signs a body
    // WITHOUT this tag) can never be reinterpreted as a revocation, and vice-versa.
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("aether-device-revocation-v1");

    /// <summary>
    /// The canonical signed body (everything but the signature): domain · version · device_id(u16 len +
    /// utf8) · device_public_key(32) · revoked_at_ms(i64 LE) · reason(u16 len + utf8).
    /// </summary>
    public static byte[] SignedBody(string deviceId, byte[] devicePublicKey, long revokedAtMs, string reason)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(devicePublicKey);
        ArgumentNullException.ThrowIfNull(reason);
        if (devicePublicKey.Length != 32)
            throw new ArgumentException("Device public key must be 32 bytes.", nameof(devicePublicKey));

        var id = Encoding.UTF8.GetBytes(deviceId);
        if (id.Length > ushort.MaxValue) throw new ArgumentException("DeviceId is too long.", nameof(deviceId));
        var why = Encoding.UTF8.GetBytes(reason);
        if (why.Length > ushort.MaxValue) throw new ArgumentException("Reason is too long.", nameof(reason));

        var body = new byte[Domain.Length + 1 + 2 + id.Length + 32 + 8 + 2 + why.Length];
        var span = body.AsSpan();
        var o = 0;
        Domain.CopyTo(span.Slice(o)); o += Domain.Length;
        span[o++] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)id.Length); o += 2;
        id.CopyTo(span.Slice(o)); o += id.Length;
        devicePublicKey.CopyTo(span.Slice(o)); o += 32;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o, 8), revokedAtMs); o += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)why.Length); o += 2;
        why.CopyTo(span.Slice(o));
        return body;
    }

    /// <summary>Creates a revocation signed by the user's 32-byte Ed25519 identity private key.</summary>
    public static DeviceRevocation Create(
        string deviceId, byte[] devicePublicKey, long revokedAtMs, string reason, byte[] identityPrivateKey)
    {
        var body = SignedBody(deviceId, devicePublicKey, revokedAtMs, reason);
        var signature = Ed25519SigningService.Sign(identityPrivateKey, body);
        return new DeviceRevocation(deviceId, devicePublicKey, revokedAtMs, reason, signature);
    }

    /// <summary>True if <paramref name="revocation"/> was signed by the identity behind
    /// <paramref name="identityPublicKey"/> — i.e. that user really did revoke this device.</summary>
    public static bool Verify(DeviceRevocation revocation, byte[] identityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(identityPublicKey);
        if (revocation.Signature is not { Length: 64 }) return false;
        if (revocation.DevicePublicKey is not { Length: 32 }) return false;

        var body = SignedBody(revocation.DeviceId, revocation.DevicePublicKey, revocation.RevokedAtMs, revocation.Reason);
        return Ed25519SigningService.Verify(identityPublicKey, body, revocation.Signature);
    }

    /// <summary>Serializes a revocation as its signed body followed by the 64-byte signature.</summary>
    public static byte[] Serialize(DeviceRevocation revocation)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        if (revocation.Signature is not { Length: 64 })
            throw new ArgumentException("Signature must be 64 bytes.", nameof(revocation));

        var body = SignedBody(revocation.DeviceId, revocation.DevicePublicKey, revocation.RevokedAtMs, revocation.Reason);
        var buffer = new byte[body.Length + 64];
        body.CopyTo(buffer, 0);
        revocation.Signature.CopyTo(buffer, body.Length);
        return buffer;
    }

    /// <summary>Parses a serialized revocation, validating framing.</summary>
    public static DeviceRevocation Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var span = data.AsSpan();
        var o = 0;

        var domainLen = Domain.Length;
        if (span.Length < domainLen + 1 + 2 + 32 + 8 + 2 + 64) throw new FormatException("DeviceRevocation is too short.");
        if (!span.Slice(0, domainLen).SequenceEqual(Domain)) throw new FormatException("Not a DeviceRevocation (domain mismatch).");
        o += domainLen;
        if (span[o++] != FormatVersion) throw new FormatException("Unsupported DeviceRevocation format version.");

        var idLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(o, 2)); o += 2;
        if (o + idLen + 32 + 8 + 2 > span.Length) throw new FormatException("DeviceRevocation is truncated.");
        var deviceId = Encoding.UTF8.GetString(span.Slice(o, idLen)); o += idLen;
        var devicePublicKey = span.Slice(o, 32).ToArray(); o += 32;
        var revokedAtMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
        var reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(o, 2)); o += 2;
        if (o + reasonLen + 64 > span.Length) throw new FormatException("DeviceRevocation is truncated.");
        var reason = Encoding.UTF8.GetString(span.Slice(o, reasonLen)); o += reasonLen;
        var signature = span.Slice(o, 64).ToArray();

        return new DeviceRevocation(deviceId, devicePublicKey, revokedAtMs, reason, signature);
    }
}

/// <summary>
/// The set of device keys a user has revoked, built by ingesting verified <see cref="DeviceRevocation"/>
/// records. A peer consults it to drop a revoked device from the "self" set and to refuse to open or
/// continue a session keyed to a revoked device. Only revocations that <see cref="DeviceRevocationCodec.Verify"/>
/// against the expected identity key are admitted, so a forwarder cannot revoke someone else's device.
/// </summary>
public sealed class RevocationSet
{
    private readonly byte[] _identityPublicKey;
    private readonly object _gate = new();
    // Keyed by the revoked device public key (hex), value = earliest verified revocation time.
    private readonly Dictionary<string, long> _revoked = new(StringComparer.Ordinal);

    /// <param name="identityPublicKey">The user's long-term identity public key; only revocations it
    /// signed are admitted.</param>
    public RevocationSet(byte[] identityPublicKey)
    {
        ArgumentNullException.ThrowIfNull(identityPublicKey);
        _identityPublicKey = (byte[])identityPublicKey.Clone();
    }

    /// <summary>
    /// Admit a revocation if it is validly signed by the identity. Returns true if it was accepted (and
    /// newly recorded or updated to an earlier time). A forged or wrongly-signed revocation is ignored.
    /// </summary>
    public bool Ingest(DeviceRevocation revocation)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        if (!DeviceRevocationCodec.Verify(revocation, _identityPublicKey)) return false;

        var key = Convert.ToHexString(revocation.DevicePublicKey);
        lock (_gate)
        {
            if (_revoked.TryGetValue(key, out var when) && when <= revocation.RevokedAtMs) return false;
            _revoked[key] = revocation.RevokedAtMs;
            return true;
        }
    }

    /// <summary>Whether this device public key has been revoked.</summary>
    public bool IsRevoked(byte[] devicePublicKey)
    {
        ArgumentNullException.ThrowIfNull(devicePublicKey);
        var key = Convert.ToHexString(devicePublicKey);
        lock (_gate) return _revoked.ContainsKey(key);
    }

    /// <summary>How many distinct devices are revoked.</summary>
    public int Count
    {
        get { lock (_gate) return _revoked.Count; }
    }
}
