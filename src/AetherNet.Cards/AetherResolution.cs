// SPDX-License-Identifier: MIT

namespace AetherNet.Cards;

/// <summary>
/// The outcome of resolving an <c>aether://</c> URI — a closed set: a resolved card, a content target to
/// fetch, a well-formed address that resolved to nothing, or a malformed address.
/// </summary>
public abstract record AetherResolution
{
    private AetherResolution() { }

    /// <summary>The URI addressed a card, resolved and verified.</summary>
    public sealed record CardResolved(Card Card) : AetherResolution;

    /// <summary>The URI addressed raw content by hash; fetch it via <c>IContentService</c>.</summary>
    public sealed record ContentTarget(string Tag, string RootHash) : AetherResolution;

    /// <summary>The URI was well-formed but nothing resolved (unknown name, no holder, or wrong owner).</summary>
    public sealed record NotFound(string Reason) : AetherResolution;

    /// <summary>The input was not a resolvable <c>aether://</c> address.</summary>
    public sealed record Invalid(string Error) : AetherResolution;
}
