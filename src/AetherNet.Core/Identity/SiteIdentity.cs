// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.Identity;

/// <summary>
/// A per-site identity — a stable, pseudonymous handle a visitor presents to one card origin, derived
/// so that it reveals neither the visitor's master tag nor their handle at any other site.
///
/// <para>
/// The problem: if a card could see the reader's real <see cref="AetherNetTag"/>, then every site the
/// reader visits could log that one identifier, and any two sites could collude to build a single
/// cross-site trail — the exact surveillance the mesh exists to deny. So a site never sees the master
/// tag. It sees a handle derived from (this device's identity, that site) and nothing else:
/// <list type="bullet">
///   <item><description><b>Stable per site</b> — the same visitor returning to the same site presents the
///     same <see cref="Pseudonym"/>, so a site can recognise a returning reader.</description></item>
///   <item><description><b>Unlinkable across sites</b> — a different site derives a different, unrelated
///     pseudonym, so two sites cannot tell they are looking at the same person.</description></item>
///   <item><description><b>Reveals nothing upward</b> — the pseudonym is a hash of a purpose-bound key
///     the node derives (<see cref="INodeIdentity.DeriveKeyAsync"/>); it cannot be reversed to the site
///     secret, and the site secret cannot be reversed to the device's root.</description></item>
/// </list>
/// </para>
///
/// <para>
/// This is the identity-layer primitive only. Exposing a pseudonym to card content is the mesh-web
/// bridge's job (Phase F1) and is deliberately not done here — a card stays inert until that surface,
/// and its security model, are decided.
/// </para>
/// </summary>
/// <param name="Pseudonym">The tag-shaped handle the site sees (XXXXX-XXXXX). Public and shareable.</param>
/// <param name="SiteSecret">The 32-byte per-site secret, for a future per-site signature/auth. Never leaves the node.</param>
public sealed record SiteIdentity(string Pseudonym, byte[] SiteSecret);

/// <summary>Derives <see cref="SiteIdentity"/> values from a node identity, one per site.</summary>
public static class SiteIdentityDerivation
{
    // Domain-separation prefix so a site key can never collide with the routing key or any other
    // purpose-bound key the node derives. Versioned so the derivation can change without ambiguity.
    private const string PurposePrefix = "aether-site-identity-v1:";

    // Crockford base-32, same alphabet as AetherNetTag/ERID (no I/L/O/U).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// The per-site identity this device presents to the site identified by <paramref name="siteTag"/>
    /// (the card author's tag). Deterministic: same device + same site always yields the same result.
    /// </summary>
    /// <param name="identity">The device identity to derive from — never asked for its root key.</param>
    /// <param name="siteTag">The site's AetherTag (the card author). Canonicalised; must be a valid tag.</param>
    public static async ValueTask<SiteIdentity> ForSiteAsync(
        INodeIdentity identity, string siteTag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!AetherNetTag.TryParse(siteTag, out var tag))
            throw new ArgumentException($"'{siteTag}' is not a valid AetherTag.", nameof(siteTag));

        var secret = await identity.DeriveKeyAsync(PurposePrefix + tag.Value, cancellationToken).ConfigureAwait(false);
        return new SiteIdentity(PseudonymFor(secret), secret);
    }

    /// <summary>
    /// The public pseudonym for a per-site secret: a tag-shaped handle over SHA-256 of the secret. Exposed
    /// for tests and for a bridge that holds the secret already; a site secret cannot be recovered from it.
    /// </summary>
    public static string PseudonymFor(byte[] siteSecret)
    {
        ArgumentNullException.ThrowIfNull(siteSecret);
        if (siteSecret.Length == 0)
            throw new ArgumentException("siteSecret cannot be empty.", nameof(siteSecret));

        var hash = SHA256.HashData(siteSecret);
        // 10 Crockford chars (50 bits), formatted like an AetherTag: XXXXX-XXXXX.
        Span<char> chars = stackalloc char[11];
        var bit = 0;
        for (var i = 0; i < 11; i++)
        {
            if (i == 5) { chars[i] = '-'; continue; }
            var value = 0;
            for (var b = 0; b < 5; b++, bit++)
            {
                var source = hash[bit >> 3];
                var taken = (source >> (7 - (bit & 7))) & 1;
                value = (value << 1) | taken;
            }
            chars[i] = Alphabet[value];
        }
        return new string(chars);
    }
}
