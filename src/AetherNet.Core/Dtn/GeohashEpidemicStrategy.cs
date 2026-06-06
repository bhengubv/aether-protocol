// SPDX-License-Identifier: MIT

using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Default epidemic strategy. SOS bundles replicate to every eligible DTN-carrier peer up to the copy cap.
/// Normal bundles prefer peers whose geohash shares a longer prefix with the recipient's last known
/// geohash than the local node — i.e. peers that are at least as close to the recipient as we are.
/// Ties broken by peer reliability score.
/// </summary>
public sealed class GeohashEpidemicStrategy : IBundleReplicationStrategy
{
    public IReadOnlyList<string> SelectTargets(DtnBundle bundle, IReadOnlyList<PeerInfo> connectedPeers, string? localGeohash)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(connectedPeers);

        var slots = bundle.MaxCopies - bundle.CopyCount;
        if (slots <= 0) return Array.Empty<string>();

        var eligible = connectedPeers
            .Where(p => !p.IsBlocked
                        && (p.Capabilities & NodeCapabilities.DtnCarrier) == NodeCapabilities.DtnCarrier
                        && !string.IsNullOrEmpty(p.Uhid)
                        && p.Uhid != bundle.SenderUhid)
            .ToArray();
        if (eligible.Length == 0) return Array.Empty<string>();

        if (bundle.Priority == BundlePriority.Sos)
            return eligible.Take(slots).Select(p => p.Uhid).ToArray();

        if (!string.IsNullOrEmpty(bundle.RecipientLastGeohash))
        {
            var localProximity = SharedPrefix(localGeohash, bundle.RecipientLastGeohash);
            return eligible
                .Select(p => new
                {
                    Peer = p,
                    Proximity = SharedPrefix(p.Geohash, bundle.RecipientLastGeohash),
                })
                .Where(x => x.Proximity >= localProximity)
                .OrderByDescending(x => x.Proximity)
                .ThenByDescending(x => x.Peer.ReliabilityScore)
                .Take(slots)
                .Select(x => x.Peer.Uhid)
                .ToArray();
        }

        return eligible
            .OrderByDescending(p => p.ReliabilityScore)
            .Take(slots)
            .Select(p => p.Uhid)
            .ToArray();
    }

    private static int SharedPrefix(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return 0;
        var min = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < min && a[i] == b[i]) i++;
        return i;
    }
}
