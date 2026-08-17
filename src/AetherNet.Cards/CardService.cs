// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNet.Cards;

/// <inheritdoc />
public sealed class CardService : ICardService
{
    private readonly IContentService _content;
    private readonly IDirectoryService _directory;

    public CardService(IContentService content, IDirectoryService directory)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async Task<Card> PublishCardAsync(string name, byte[] content, string contentType, byte[] authorPrivateKey, long version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorPrivateKey);

        return await PublishAsync(
            name, content, contentType, version,
            Ed25519SigningService.DerivePublicKey(authorPrivateKey),
            binding => Task.FromResult(Ed25519SigningService.Sign(authorPrivateKey, binding)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish a card authored by this device, without ever being handed the device's key.
    ///
    /// <para>
    /// Preferred over the key-taking overload. A card's author is the device — its AetherTag is what a
    /// reader verifies against — but publishing does not need the secret itself, only a signature over
    /// the binding. Asking the node for that signature keeps the identity where it belongs instead of
    /// copying it into every component that wants to publish something.
    /// </para>
    /// </summary>
    public async Task<Card> PublishCardAsync(string name, byte[] content, string contentType, INodeIdentity author, long version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);

        var authorPublicKey = await author.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);

        return await PublishAsync(
            name, content, contentType, version, authorPublicKey,
            async binding => await author.SignAsync(binding, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one publish path. Both overloads differ only in <i>who holds the pen</i> — the bytes that get
    /// content-addressed, signed, bound and announced are identical, so a card is the same card however
    /// its author was named.
    /// </summary>
    private async Task<Card> PublishAsync(
        string name, byte[] content, string contentType, long version,
        byte[] authorPublicKey, Func<byte[], Task<byte[]>> sign, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);

        // Content-address the blob — persists the chunks locally and yields the descriptor whose
        // RootHash is the tamper-evident content identity.
        var descriptor = await _content.PublishAsync(name, content, contentType, cancellationToken: cancellationToken).ConfigureAwait(false);

        var signature = await sign(BindingBytes(authorPublicKey, name, version, descriptor.RootHash)).ConfigureAwait(false);

        // Publish under the plain card name — the directory derives the ownership scope (the author's
        // tag) and files the binding under a slot only this author can occupy.
        await _directory.PublishSignedAsync(name, descriptor, authorPublicKey, version, signature, cancellationToken).ConfigureAwait(false);

        // Announce content availability so peers can pull the chunks.
        await _content.AnnounceAsync(descriptor, cancellationToken).ConfigureAwait(false);

        return new Card(name, version, authorPublicKey, descriptor, signature);
    }

    public Task<Card?> ResolveCardAsync(byte[] authorPublicKey, string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorPublicKey);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ResolveCardByTagAsync(AetherNetTag.FromPublicKey(authorPublicKey).Value, name, queryTimeout, cancellationToken);
    }

    public async Task<Card?> ResolveCardByTagAsync(string tag, string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!AetherNetTag.TryParse(tag, out var parsed))
        {
            return null;
        }

        // The directory scopes the slot by the tag, so only a binding whose author derives to this tag
        // could be filed here. We re-check ownership anyway as defence in depth.
        var binding = await _directory.ResolveBindingByScopeAsync(parsed.Value, name, queryTimeout, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.Authenticated || binding.AuthorPublicKey is null || binding.Signature is null)
        {
            return null;
        }
        if (!AetherNetTag.Verify(parsed.Value, binding.AuthorPublicKey))
        {
            return null;
        }

        return new Card(name, binding.Version, binding.AuthorPublicKey, binding.Descriptor, binding.Signature);
    }

    public bool VerifyCard(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // Content integrity: the descriptor's chunk hashes must recompute to its root hash.
        if (!card.Descriptor.VerifySelf())
        {
            return false;
        }

        var body = NameBindingCodec.BuildSignableBody(
            NameHashing.Hash(card.Name), card.AuthorPublicKey, card.Version, card.Descriptor.RootHash);
        return Ed25519SigningService.Verify(card.AuthorPublicKey, body, card.Signature);
    }

    /// <summary>
    /// The exact bytes an author signs to bind a name to content. Shared by both publish paths so the
    /// signature does not depend on how the author was named.
    /// </summary>
    private static byte[] BindingBytes(byte[] authorPublicKey, string name, long version, string rootHash) =>
        NameBindingCodec.BuildSignableBody(NameHashing.Hash(name), authorPublicKey, version, rootHash);
}
