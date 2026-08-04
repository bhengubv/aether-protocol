// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNet.Cards;

/// <summary>
/// Ed25519 + AetherTag implementation of <see cref="INameBindingVerifier"/> — the bridge that lets the
/// crypto-free <c>AetherNet.Content</c> directory authenticate signed bindings and scope them by owner
/// without depending on a signing/identity library. Wire this into a node (e.g. via <c>AddCards()</c>).
/// </summary>
public sealed class Ed25519NameBindingVerifier : INameBindingVerifier
{
    public bool Verify(byte[] authorPublicKey, byte[] signedBody, byte[] signature)
        => Ed25519SigningService.Verify(authorPublicKey, signedBody, signature);

    /// <summary>The ownership scope of a key is its AetherTag — a one-way derivation the directory uses to
    /// file the author's bindings under a slot only that author can occupy.</summary>
    public string DeriveScope(byte[] authorPublicKey)
        => AetherNetTag.FromPublicKey(authorPublicKey).Value;
}
