// SPDX-License-Identifier: MIT

using AetherNet.Content.Models;
using AetherNet.Protocol;

namespace AetherNet.Content;

/// <summary>
/// Application-layer name → <see cref="ContentDescriptor"/> resolver. Closes the
/// Wave-16 protocol gap: <see cref="IContentService"/> is content-addressed
/// (<c>rootHash</c>-keyed) — consumers that want to fetch content by an
/// application-layer name (e.g. <c>"podcast:abc123"</c>, <c>"reel:hash"</c>,
/// <c>"album:artist/title"</c>) cannot do so via <see cref="IContentService"/>
/// alone because they do not know the <c>rootHash</c> upfront. That's precisely
/// what they're trying to discover.
///
/// <para>
/// This service maintains a local name catalogue, broadcasts
/// <see cref="PacketType.NamePublish"/> when the local node publishes a binding,
/// emits <see cref="PacketType.NameQuery"/> when the local node needs to resolve
/// an unknown name, and unicasts a <see cref="PacketType.NamePublish"/> response
/// when a peer's query matches an entry we hold.
/// </para>
///
/// <para>Added in v1.2.0. Closes Issue #60 — see <c>OPEN_ISSUES.md</c>.</para>
/// </summary>
public interface IDirectoryService
{
    /// <summary>
    /// Raised when a <see cref="PacketType.NamePublish"/> packet arrives —
    /// either an unsolicited broadcast from a peer or a unicast response to one
    /// of our outstanding queries — and updates the local catalogue.
    /// </summary>
    event EventHandler<DirectoryEntryAnnouncedEventArgs>? EntryAnnounced;

    /// <summary>
    /// Store the binding locally and broadcast a <see cref="PacketType.NamePublish"/>
    /// to every connected peer. Subsequent <see cref="ResolveAsync"/> calls on the
    /// local node return the descriptor immediately from the catalogue.
    /// </summary>
    Task PublishAsync(string name, ContentDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a name to its descriptor. Returns the local-catalogue hit immediately
    /// if present. Otherwise broadcasts a <see cref="PacketType.NameQuery"/> and awaits
    /// a matching <see cref="PacketType.NamePublish"/> response up to
    /// <paramref name="queryTimeout"/> (default 5 seconds). Returns null on timeout.
    /// </summary>
    Task<ContentDescriptor?> ResolveAsync(string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>Enumerate every name currently in the local catalogue (snapshot).</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pump inbound <see cref="PacketType.NamePublish"/> / <see cref="PacketType.NameQuery"/>
    /// packets into the service. Hosts wire this from their transport's receive pump.
    /// </summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}
