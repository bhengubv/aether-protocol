// SPDX-License-Identifier: MIT

using AetherNet.Addressing;
using AetherNet.Identity;

namespace AetherNet.Cards;

/// <inheritdoc />
public sealed class AetherResolver : IAetherResolver
{
    // Reserved path handler: aether://<tag>/content/<rootHash> addresses raw content by hash.
    private const string ContentPrefix = "content/";

    private readonly ICardService _cards;

    public AetherResolver(ICardService cards)
        => _cards = cards ?? throw new ArgumentNullException(nameof(cards));

    public Task<AetherResolution> ResolveAsync(string uri, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        if (!AetherUri.TryParse(uri, out var parsed, out var error))
        {
            return Task.FromResult<AetherResolution>(new AetherResolution.Invalid(error ?? "Malformed aether URI."));
        }
        return ResolveAsync(parsed, queryTimeout, cancellationToken);
    }

    public async Task<AetherResolution> ResolveAsync(AetherUri uri, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        if (!uri.IsValid)
        {
            return new AetherResolution.Invalid("URI is not a valid aether:// address.");
        }

        // Card and content resolution are scoped to an AetherTag authority. A raw 64-hex UHID authority
        // is a valid address but is not resolved to a card in this build.
        if (!AetherNetTag.TryParse(uri.Authority, out var tag))
        {
            return new AetherResolution.Invalid(
                $"Authority '{uri.Authority}' is not an AetherTag; only tag authorities resolve here.");
        }

        if (string.IsNullOrEmpty(uri.Path))
        {
            return new AetherResolution.NotFound($"{uri.Authority} has no resource path.");
        }

        // Reserved 'content/<hash>' → a content-addressed target the caller fetches via IContentService.
        if (uri.Path.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            var rootHash = uri.Path[ContentPrefix.Length..];
            return string.IsNullOrEmpty(rootHash)
                ? new AetherResolution.NotFound("Empty content hash.")
                : new AetherResolution.ContentTarget(tag.Value, rootHash);
        }

        // Otherwise the path names a card owned by the tag.
        var card = await _cards.ResolveCardByTagAsync(tag.Value, uri.Path, queryTimeout, cancellationToken).ConfigureAwait(false);
        return card is null
            ? new AetherResolution.NotFound($"No verified card '{uri.Path}' for {tag.Value}.")
            : new AetherResolution.CardResolved(card);
    }
}
