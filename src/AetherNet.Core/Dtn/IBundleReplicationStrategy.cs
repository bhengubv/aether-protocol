// SPDX-License-Identifier: MIT

using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Decides which connected peers should receive a copy of a bundle on the
/// next replication pass. Hosts can swap in routing strategies tuned to their
/// deployment (geohash proximity, social trust, prior delivery success, etc.).
/// The default <see cref="GeohashEpidemicStrategy"/> matches the private CircleAether implementation.
/// </summary>
public interface IBundleReplicationStrategy
{
    /// <summary>
    /// Selects up to <see cref="DtnBundle.MaxCopies"/> minus <see cref="DtnBundle.CopyCount"/> peers from
    /// <paramref name="connectedPeers"/> that should receive a replica of <paramref name="bundle"/>.
    /// Returns peer UHIDs in the order they should be tried.
    /// </summary>
    /// <param name="bundle">The bundle awaiting replication.</param>
    /// <param name="connectedPeers">Currently connected peers.</param>
    /// <param name="localGeohash">The local node's geohash, when known. Used by proximity-aware strategies.</param>
    IReadOnlyList<string> SelectTargets(DtnBundle bundle, IReadOnlyList<PeerInfo> connectedPeers, string? localGeohash);
}
