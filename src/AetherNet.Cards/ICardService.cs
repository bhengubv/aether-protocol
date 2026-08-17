// SPDX-License-Identifier: MIT

namespace AetherNet.Cards;

/// <summary>
/// Publishes and resolves <see cref="Card"/>s over the mesh: content-addresses the blob via
/// <c>IContentService</c>, and binds the author-scoped name to it via a signed, versioned
/// <c>IDirectoryService</c> entry.
/// </summary>
public interface ICardService
{
    /// <summary>
    /// Publish a card: content-address <paramref name="content"/>, sign the
    /// {nameHash, authorPublicKey, <paramref name="version"/>, rootHash} binding with
    /// <paramref name="authorPrivateKey"/>, and broadcast the authenticated name binding.
    /// </summary>
    Task<Card> PublishCardAsync(string name, byte[] content, string contentType, byte[] authorPrivateKey, long version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a card authored by this device, naming the node rather than handing over its key.
    ///
    /// <para>
    /// Preferred over the overload above. Publishing needs a <i>signature</i> over the name binding, not
    /// the secret that produces it — so the node signs and the identity stays where it belongs. Handing
    /// a private key to every component that wants to publish is how a device's identity ends up copied
    /// into places that have no business holding it.
    /// </para>
    /// </summary>
    Task<Card> PublishCardAsync(string name, byte[] content, string contentType, AetherNet.Identity.INodeIdentity author, long version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve the card published by <paramref name="authorPublicKey"/> under <paramref name="name"/>.
    /// Returns null if not found, not authenticated, or authenticated by a different author. The card's
    /// content bytes are fetched separately via <c>IContentService</c> using the descriptor's root hash.
    /// </summary>
    Task<Card?> ResolveCardAsync(byte[] authorPublicKey, string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve the card published under <paramref name="name"/> by the owner of AetherTag
    /// <paramref name="tag"/> — the form the <c>aether://</c> resolver uses (it holds the tag, not the
    /// key). Returns null if not found, not authenticated, or if the resolved binding's author key does
    /// not derive to <paramref name="tag"/>.
    /// </summary>
    Task<Card?> ResolveCardByTagAsync(string tag, string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Standalone verification of a card: the Ed25519 signature over its canonical binding body AND the
    /// descriptor's self-integrity (chunk hashes recompute to the root hash). True iff both hold.
    /// </summary>
    bool VerifyCard(Card card);
}
