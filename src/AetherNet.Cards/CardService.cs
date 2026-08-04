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
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(authorPrivateKey);

        var authorPublicKey = Ed25519SigningService.DerivePublicKey(authorPrivateKey);

        // Content-address the blob — persists the chunks locally and yields the descriptor whose
        // RootHash is the tamper-evident content identity.
        var descriptor = await _content.PublishAsync(name, content, contentType, cancellationToken: cancellationToken).ConfigureAwait(false);

        var signature = SignBinding(authorPublicKey, authorPrivateKey, name, version, descriptor.RootHash);

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

    private static byte[] SignBinding(byte[] authorPublicKey, byte[] authorPrivateKey, string name, long version, string rootHash)
    {
        var body = NameBindingCodec.BuildSignableBody(NameHashing.Hash(name), authorPublicKey, version, rootHash);
        return Ed25519SigningService.Sign(authorPrivateKey, body);
    }
}
