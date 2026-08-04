// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Content;

/// <summary>
/// Canonical salted hashing of an application-layer directory name for the wire. The plaintext
/// name never leaves a node; peers match on this salted hash (private, DNS-style resolution).
/// Shared by <see cref="DirectoryService"/> and the signed-binding (card) layer so both sides
/// derive the identical wire key for the same name.
/// </summary>
public static class NameHashing
{
    // PRIVACY: domain-separation prefix for the name hash that travels on the wire.
    public const string NameHashSalt = "aether-dir-name-v1:";

    /// <summary>
    /// Salted SHA-256 of an application name, hex-encoded lowercase. Names are case-sensitive
    /// opaque identifiers — the exact UTF-8 bytes are hashed, with no normalisation.
    /// </summary>
    public static string Hash(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(NameHashSalt + name)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The storage slot for a scoped (authenticated) binding — <c>SHA-256(salt ‖ scope ‖ innerNameHash)</c>,
    /// hex-encoded lowercase. The <paramref name="scope"/> is the author's ownership namespace, derived by
    /// the directory <em>from the signing key</em> (see <see cref="INameBindingVerifier.DeriveScope"/>), so
    /// only the scope's owner can produce a binding filed here. An impostor's binding for the same
    /// <paramref name="innerNameHash"/> lands in the impostor's own scope slot — never the owner's — which
    /// is what makes name-slot squatting impossible.
    /// </summary>
    public static string ScopedSlot(string scope, string innerNameHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(innerNameHash);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(NameHashSalt + "scope:" + scope + ":" + innerNameHash)))
            .ToLowerInvariant();
    }
}
