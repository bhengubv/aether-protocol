// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Routing;

/// <summary>
/// Turns the address on a received packet — which, under the negotiated <c>erid-routing</c> capability,
/// is a rotating <see cref="EphemeralRoutingId">ERID</see> rather than the stable UHID — into the
/// long-term identity the routing table and the reputation/incentive ledgers must key on.
///
/// <para>
/// This is the seam that keeps two invariants true at once:
/// <list type="bullet">
///   <item><description><b>E1 — route tables keyed on ERID, TTL ≤ epoch.</b> The table stays keyed on a
///     STABLE identity (a route survives ERID rotation instead of vanishing every window), while a route
///     learned from an ERID is capped at the epoch boundary (<see cref="RouteAddress.EpochExpiryUtc"/>),
///     because the wire address it was learned from stops being valid then.</description></item>
///   <item><description><b>E3 — reputation/incentive on long-term identity, never the wire ERID.</b> A
///     caller that keys any ledger on <see cref="RouteAddress.StableUhid"/> can never attribute
///     behaviour to a rotating value an observer could correlate.</description></item>
/// </list>
/// </para>
///
/// <para>
/// It is opportunistic and fail-safe. Given an <see cref="EridDirectory"/> it tries to resolve the
/// address as a known peer's current-epoch ERID; if that fails — the address is a plain UHID, or the
/// peer is unknown — it returns the address unchanged. With no directory it is a pure pass-through, so a
/// node that has not negotiated ERID routing behaves exactly as it does today. Nothing on the wire has
/// to change for this to be correct; it simply becomes load-bearing once the header swap (E2) lands.
/// </para>
/// </summary>
public sealed class EridRouteResolver
{
    private readonly EridDirectory? _directory;
    private readonly int _epochSeconds;
    private readonly Func<long> _nowUnixSeconds;

    /// <summary>
    /// Creates a resolver. With <paramref name="directory"/> null it is a pass-through (the default a
    /// node uses until it has negotiated ERID routing).
    /// </summary>
    /// <param name="directory">The peer-key directory that resolves ERIDs, or null for pass-through.</param>
    /// <param name="epochSeconds">Rotation window; must match the ERID epoch (default 15 min).</param>
    /// <param name="nowUnixSeconds">Clock, injectable for tests. Defaults to wall-clock UTC seconds.</param>
    public EridRouteResolver(
        EridDirectory? directory = null,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds,
        Func<long>? nowUnixSeconds = null)
    {
        if (epochSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochSeconds), "epochSeconds must be positive.");
        _directory = directory;
        _epochSeconds = epochSeconds;
        _nowUnixSeconds = nowUnixSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>Whether this resolver can turn ERIDs into identities at all (a directory is present).</summary>
    public bool Enabled => _directory is not null;

    /// <summary>
    /// Resolves a received wire address to the stable identity to key on. When the address is a known
    /// peer's current-epoch ERID, <see cref="RouteAddress.StableUhid"/> is that peer's long-term UHID,
    /// <see cref="RouteAddress.WasErid"/> is true, and <see cref="RouteAddress.EpochExpiryUtc"/> is the
    /// instant the ERID rotates. Otherwise the address passes through unchanged with no epoch cap.
    /// </summary>
    public RouteAddress Resolve(string wireAddress)
    {
        if (_directory is null || string.IsNullOrEmpty(wireAddress))
            return new RouteAddress(wireAddress, WasErid: false, EpochExpiryUtc: null);

        var now = _nowUnixSeconds();
        var peer = _directory.ResolvePeer(wireAddress, now);
        if (peer is null)
            return new RouteAddress(wireAddress, WasErid: false, EpochExpiryUtc: null);

        var epochEnd = DateTimeOffset
            .FromUnixTimeSeconds(EphemeralRoutingId.EpochEndUnixSeconds(now, _epochSeconds))
            .UtcDateTime;
        return new RouteAddress(peer, WasErid: true, EpochExpiryUtc: epochEnd);
    }
}

/// <summary>
/// The stable identity a received wire address resolves to, plus whether it came from an ERID and, if
/// so, when that ERID rotates (the latest a route learned from it may be trusted).
/// </summary>
/// <param name="StableUhid">The long-term UHID to key routes and ledgers on.</param>
/// <param name="WasErid">True when the wire address was a resolvable ERID rather than a plain UHID.</param>
/// <param name="EpochExpiryUtc">When the ERID rotates; null when the address was not an ERID.</param>
public readonly record struct RouteAddress(string StableUhid, bool WasErid, DateTime? EpochExpiryUtc);
