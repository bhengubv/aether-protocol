// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace Aether.Identity;

/// <summary>
/// Aether Tag — the human-readable, shareable identity address for an Aether node.
///
/// <h3>What it is</h3>
/// An Aether Tag is the public face of a node's Ed25519 identity key: a short,
/// memorable string a person can share verbally, print on a business card, or put
/// in a social bio. It answers the question "how do I reach you on the free mesh?"
/// without exposing any private information — no phone number, no email, no carrier.
///
/// <h3>Derivation</h3>
/// <code>
/// SHA-256(Ed25519IdentityPublicKey)[0..49 bits] → 10 Crockford base-32 chars → "XXXXX-XXXXX"
/// </code>
/// <list type="bullet">
///   <item><description>Input: the node's 32-byte Ed25519 identity public key.</description></item>
///   <item><description>SHA-256 provides uniform distribution across the output space.</description></item>
///   <item><description>50 bits → ~1.1 quadrillion combinations — collision-resistant at global scale.</description></item>
///   <item><description>Crockford base-32 alphabet (0–9, A–Z minus I, L, O, U) eliminates
///     visual ambiguity between similar glyphs.</description></item>
///   <item><description>Formatted as XXXXX-XXXXX (two groups of five, case-insensitive) — readable
///     aloud, typeable without error, fits a tweet.</description></item>
/// </list>
///
/// <h3>Security properties</h3>
/// <list type="bullet">
///   <item><description>The tag is a one-way hash of the public key — the public key cannot
///     be recovered from the tag.</description></item>
///   <item><description>Ownership is proved by possessing the corresponding private key; the private
///     key is protected by biometric authentication on the device.</description></item>
///   <item><description>A stolen device does not grant the attacker the ability to sign, send,
///     or decrypt as the owner — the biometric gate on the private key prevents it.</description></item>
///   <item><description>Use <see cref="Verify"/> to confirm that a tag belongs to a given public
///     key before establishing trust.</description></item>
/// </list>
///
/// <h3>Cross-device portability</h3>
/// An Aether Tag follows the person, not the hardware. Moving to a new device transfers
/// the Ed25519 keypair (signed by the old device + biometric auth on both ends). The tag
/// value does not change. Contacts do not need to be notified.
///
/// <h3>Format</h3>
/// <c>KXJB7-MN2P4</c> — 11 characters (10 data + 1 separator). Accepted inputs are
/// case-insensitive and may omit the separator: <c>kxjb7mn2p4</c>, <c>KXJB7MN2P4</c>,
/// and <c>KXJB7-MN2P4</c> all parse to the same tag.
/// </summary>
public readonly struct AetherTag : IEquatable<AetherTag>
{
    // ── Crockford base-32 ────────────────────────────────────────────────────
    // Removes I, L, O, U to eliminate visual ambiguity (0/O, 1/I/L, U/V).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int DataLength = 10;    // significant characters
    private const int GroupSize  = 5;     // characters per display group
    private const int BitCount   = 50;    // 10 groups × 5 bits
    private const string Separator = "-";

    // ── State ────────────────────────────────────────────────────────────────
    /// <summary>
    /// The canonical formatted tag string (XXXXX-XXXXX).
    /// Empty string if this is a default (uninitialized) instance.
    /// </summary>
    public string Value { get; }

    /// <summary>Returns true if this instance was produced by <see cref="FromPublicKey"/> or
    /// <see cref="Parse"/> rather than being a default struct value.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Value);

    private AetherTag(string value) => Value = value;

    // ── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives an Aether Tag from a 32-byte Ed25519 identity public key.
    /// </summary>
    /// <param name="publicKey">The node's 32-byte Ed25519 identity public key.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="publicKey"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="publicKey"/> is not 32 bytes.</exception>
    public static AetherTag FromPublicKey(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (publicKey.Length != 32)
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(publicKey));

        // SHA-256 for uniform distribution over the output space.
        byte[] hash = SHA256.HashData(publicKey);

        // Pack the first 50 bits into the low 50 bits of a ulong.
        // Bytes 0–5 contribute 48 bits; the top 2 bits of byte 6 contribute 2 more.
        ulong bits = ((ulong)hash[0] << 42)
                   | ((ulong)hash[1] << 34)
                   | ((ulong)hash[2] << 26)
                   | ((ulong)hash[3] << 18)
                   | ((ulong)hash[4] << 10)
                   | ((ulong)hash[5] <<  2)
                   | ((ulong)(hash[6] >> 6) & 0x3UL);

        // Extract 10 × 5-bit groups, most-significant first.
        Span<char> chars = stackalloc char[DataLength];
        for (int i = DataLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(bits & 0x1F)];
            bits >>= 5;
        }

        string formatted = string.Concat(
            chars[..GroupSize],
            Separator.AsSpan(),
            chars[GroupSize..]);

        return new AetherTag(formatted);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses an Aether Tag string. Accepts with or without separator, upper or lower case.
    /// </summary>
    /// <exception cref="FormatException">Thrown if the string is not a valid Aether Tag.</exception>
    public static AetherTag Parse(string tag)
    {
        if (!TryParse(tag, out var result))
            throw new FormatException(
                $"Invalid Aether Tag: '{tag}'. Expected format XXXXX-XXXXX (Crockford base-32).");
        return result;
    }

    /// <summary>
    /// Attempts to parse an Aether Tag string. Returns false if the format is invalid.
    /// Accepts input with or without the separator, upper or lower case.
    /// </summary>
    public static bool TryParse(string? tag, out AetherTag result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(tag))
            return false;

        // Normalise: uppercase, strip separator and whitespace.
        Span<char> buf = stackalloc char[tag.Length];
        int len = 0;
        foreach (char c in tag)
        {
            if (c == '-' || c == ' ')
                continue;
            buf[len++] = char.ToUpperInvariant(c);
        }

        if (len != DataLength)
            return false;

        // Validate every character against the Crockford alphabet.
        for (int i = 0; i < len; i++)
        {
            if (Alphabet.IndexOf(buf[i]) < 0)
                return false;
        }

        string formatted = string.Concat(
            buf[..GroupSize],
            Separator.AsSpan(),
            buf[GroupSize..len]);

        result = new AetherTag(formatted);
        return true;
    }

    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a tag string corresponds to a given Ed25519 public key.
    /// Use this before accepting a claimed identity.
    /// </summary>
    /// <param name="tag">The tag string to verify.</param>
    /// <param name="publicKey">The 32-byte Ed25519 public key claimed to own the tag.</param>
    /// <returns>True if the tag is derived from the given public key.</returns>
    public static bool Verify(string tag, byte[] publicKey)
    {
        if (publicKey is not { Length: 32 })
            return false;

        if (!TryParse(tag, out var parsed))
            return false;

        return parsed == FromPublicKey(publicKey);
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(AetherTag other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is AetherTag other && Equals(other);

    public override int GetHashCode() =>
        Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(AetherTag left, AetherTag right) => left.Equals(right);
    public static bool operator !=(AetherTag left, AetherTag right) => !left.Equals(right);

    // ── Conversions ───────────────────────────────────────────────────────────

    /// <summary>Returns the canonical XXXXX-XXXXX string.</summary>
    public override string ToString() => Value ?? string.Empty;

    /// <summary>Implicitly converts to the canonical string for use in APIs that accept strings.</summary>
    public static implicit operator string(AetherTag tag) => tag.ToString();
}
