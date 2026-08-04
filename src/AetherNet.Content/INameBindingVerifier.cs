// SPDX-License-Identifier: MIT

namespace AetherNet.Content;

/// <summary>
/// Verifies the author signature on an authenticated directory name binding.
/// <para>
/// <c>AetherNet.Content</c> is deliberately crypto-free — content addressing is pure SHA-256 from the
/// BCL. Signature verification for signed name bindings is therefore injected: the cards / identity
/// layer supplies an Ed25519 implementation, so the directory can authenticate bindings without
/// <c>AetherNet.Content</c> taking a dependency on a signing library.
/// </para>
/// </summary>
public interface INameBindingVerifier
{
    /// <summary>
    /// Returns true iff <paramref name="signature"/> is a valid signature by
    /// <paramref name="authorPublicKey"/> over <paramref name="signedBody"/>
    /// (see <see cref="NameBindingCodec.BuildSignableBody"/>).
    /// </summary>
    bool Verify(byte[] authorPublicKey, byte[] signedBody, byte[] signature);

    /// <summary>
    /// Derive the ownership scope an author's key controls — the namespace the directory files this
    /// author's authenticated bindings under (see <see cref="NameHashing.ScopedSlot"/>). Because the
    /// directory derives the scope from the signing key, only this author can write to its own slots: an
    /// impostor's binding lands in the impostor's scope, never the owner's. For cards this is the author's
    /// AetherTag.
    /// </summary>
    string DeriveScope(byte[] authorPublicKey);
}
