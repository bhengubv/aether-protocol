// SPDX-License-Identifier: MIT

using AetherNet.Addressing;

namespace AetherNet.Cards;

/// <summary>
/// Resolves an <c>aether://</c> URI to what it addresses on the mesh. In this build:
/// <list type="bullet">
///   <item><description><c>aether://&lt;tag&gt;/&lt;name&gt;</c> → the verified <see cref="Card"/> the
///     tag's owner published under that name (the resolved binding's key is cross-checked against the
///     tag, so a wrong-key squatter of the name slot is rejected).</description></item>
///   <item><description><c>aether://&lt;tag&gt;/content/&lt;rootHash&gt;</c> → a content target the
///     caller fetches via <c>IContentService</c>.</description></item>
/// </list>
/// The same address is answered by <em>whoever</em> carries the card — no server owns it.
/// </summary>
public interface IAetherResolver
{
    /// <summary>Resolve a parsed <see cref="AetherUri"/>.</summary>
    Task<AetherResolution> ResolveAsync(AetherUri uri, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>Parse and resolve an <c>aether://</c> URI string. A parse failure yields <c>Invalid</c>.</summary>
    Task<AetherResolution> ResolveAsync(string uri, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default);
}
