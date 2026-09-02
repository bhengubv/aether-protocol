// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNet.Security.Identity;

/// <summary>
/// A challenge to prove possession of the key behind an <see cref="AetherNetTag"/> — without the
/// verifier storing anything about the holder.
///
/// <para>
/// A tag is <c>FromPublicKey(devicePublicKey)</c>; owning it means holding the matching private key.
/// The verifier issues a fresh random nonce bound to a purpose and a moment; the holder signs it with
/// the node identity; the verifier checks the signature against the public key, confirms the public key
/// yields the expected tag, confirms freshness, and <b>discards everything</b>. Nothing about the holder
/// is retained — ownership is proven, never stored, which is the whole point: an account can prove it
/// controls a tag to a relay or a peer without that party accumulating a record it could later leak.
/// </para>
/// </summary>
/// <param name="Nonce">Random challenge bytes the holder must sign.</param>
/// <param name="IssuedAtMs">When the challenge was issued (Unix ms), for freshness.</param>
/// <param name="Purpose">What the proof is for (e.g. "device-admit", "relay-auth") — bound into the signature.</param>
public sealed record OwnershipChallenge(byte[] Nonce, long IssuedAtMs, string Purpose)
{
    /// <summary>The nonce length a fresh challenge uses.</summary>
    public const int NonceLength = 32;

    /// <summary>Issue a fresh challenge with a random nonce.</summary>
    public static OwnershipChallenge Issue(string purpose, long nowMs)
        => new(RandomNumberGenerator.GetBytes(NonceLength), nowMs, purpose ?? string.Empty);

    /// <summary>Whether the challenge is within its freshness window (and not from the future).</summary>
    public bool IsFresh(long nowMs, long maxAgeMs) => nowMs >= IssuedAtMs && nowMs - IssuedAtMs <= maxAgeMs;
}

/// <summary>A holder's answer to an <see cref="OwnershipChallenge"/>.</summary>
/// <param name="Nonce">The challenge nonce this proof answers (must equal the challenge's).</param>
/// <param name="PublicKey">The holder's 32-byte Ed25519 public key — the tag derives from it.</param>
/// <param name="Signature">64-byte Ed25519 signature over the canonical challenge body.</param>
public sealed record OwnershipProof(byte[] Nonce, byte[] PublicKey, byte[] Signature);

/// <summary>Builds ownership proofs (holder side) and checks them (verifier side).</summary>
public static class OwnershipValidator
{
    /// <summary>Default freshness window for a challenge: five minutes.</summary>
    public const long DefaultMaxAgeMs = 300_000;

    // Domain-separation tag so an ownership signature can never be a valid signature for any other
    // purpose (a device link, a tip, a route reply) and vice-versa.
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("aether-ownership-proof-v1");

    /// <summary>
    /// The canonical bytes that get signed: domain · nonce_len(u16 LE) · nonce · issued_at_ms(i64 LE) ·
    /// purpose_len(u16 LE) · purpose(utf8). Signer and verifier operate over exactly these bytes.
    /// </summary>
    public static byte[] ChallengeBody(OwnershipChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(challenge.Nonce);
        var purpose = Encoding.UTF8.GetBytes(challenge.Purpose ?? string.Empty);
        if (challenge.Nonce.Length > ushort.MaxValue) throw new ArgumentException("Nonce is too long.");
        if (purpose.Length > ushort.MaxValue) throw new ArgumentException("Purpose is too long.");

        var body = new byte[Domain.Length + 2 + challenge.Nonce.Length + 8 + 2 + purpose.Length];
        var span = body.AsSpan();
        var o = 0;
        Domain.CopyTo(span.Slice(o)); o += Domain.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)challenge.Nonce.Length); o += 2;
        challenge.Nonce.CopyTo(span.Slice(o)); o += challenge.Nonce.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o, 8), challenge.IssuedAtMs); o += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)purpose.Length); o += 2;
        purpose.CopyTo(span.Slice(o));
        return body;
    }

    /// <summary>Holder side: answer a challenge by signing it as this node.</summary>
    public static async ValueTask<OwnershipProof> ProveAsync(
        INodeIdentity identity, OwnershipChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(challenge);

        var publicKey = await identity.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var signature = await identity.SignAsync(ChallengeBody(challenge), cancellationToken).ConfigureAwait(false);
        return new OwnershipProof(challenge.Nonce, publicKey, signature);
    }

    /// <summary>
    /// Verifier side: true iff the proof answers <b>this</b> challenge, is fresh, its public key yields
    /// <paramref name="expectedTag"/>, and the signature checks out. The verifier stores nothing.
    /// </summary>
    public static bool Verify(
        OwnershipChallenge challenge, OwnershipProof proof, AetherNetTag expectedTag, long nowMs,
        long maxAgeMs = DefaultMaxAgeMs)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(proof);

        // (a) the proof must answer THIS challenge's nonce — not a replayed one
        if (proof.Nonce is null || challenge.Nonce is null) return false;
        if (!CryptographicOperations.FixedTimeEquals(proof.Nonce, challenge.Nonce)) return false;

        // (b) the challenge must be fresh
        if (!challenge.IsFresh(nowMs, maxAgeMs)) return false;

        // (c) the public key must be well-formed and yield exactly the expected tag
        if (proof.PublicKey is not { Length: 32 }) return false;
        if (proof.Signature is not { Length: 64 }) return false;
        if (!string.Equals(AetherNetTag.FromPublicKey(proof.PublicKey).Value, expectedTag.Value, StringComparison.Ordinal))
            return false;

        // (d) and the signature must verify over the canonical body
        return Ed25519SigningService.Verify(proof.PublicKey, ChallengeBody(challenge), proof.Signature);
    }
}
