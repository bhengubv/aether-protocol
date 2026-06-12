// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Identity;

/// <summary>
/// Resolves rotating <see cref="EphemeralRoutingId"/> (ERID) wire addresses to and from the stable
/// peer identities behind them — the piece that lets an ESTABLISHED relationship follow a peer's
/// rotating address while an outsider cannot.
///
/// <para>A node derives its OWN secret <c>routingKey</c> once (from its identity secret, via
/// <see cref="EphemeralRoutingId.DeriveRoutingKey"/>) and shares it with a peer <em>inside</em> the
/// established Signal session — never on the wire. Each side stores the other's routingKey here, so
/// either can compute the other's current ERID for addressing, and reverse-resolve an inbound ERID
/// back to the peer it belongs to. An outsider holds no routingKey and can do neither: to a passive
/// observer the ERID is an opaque value that rotates every epoch with no cross-window linkage.</para>
///
/// <para>Sharing the routingKey (rather than a single ERID) is deliberate: a party you have an
/// established relationship with is, by definition, allowed to recognise you over time. The ERID
/// hides you from <em>outsiders</em>, not from peers you have chosen to talk to. (A future hardening
/// can share only a bounded window of upcoming ERIDs instead, trading convenience for forward
/// unlinkability against a later-compromised peer.)</para>
///
/// <para>This is the in-memory directory only — additive, off-wire, and behind the eventual
/// <c>erid/v1</c> capability. It changes no serialized bytes on its own.</para>
/// </summary>
public sealed class EridDirectory
{
    private readonly byte[] _myRoutingKey;
    private readonly int _epochSeconds;
    private readonly int _eridLength;

    // peerUhid -> that peer's secret routingKey, learned inside an established session.
    private readonly ConcurrentDictionary<string, byte[]> _peerKeys = new(StringComparer.Ordinal);

    /// <param name="myRoutingKey">This node's secret routingKey — derive it with
    /// <see cref="EphemeralRoutingId.DeriveRoutingKey"/> from the identity secret. Copied defensively.</param>
    public EridDirectory(
        byte[] myRoutingKey,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds,
        int eridLength = EphemeralRoutingId.DefaultLength)
    {
        ArgumentNullException.ThrowIfNull(myRoutingKey);
        if (myRoutingKey.Length == 0)
            throw new ArgumentException("myRoutingKey cannot be empty.", nameof(myRoutingKey));
        if (epochSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochSeconds));

        _myRoutingKey = (byte[])myRoutingKey.Clone();
        _epochSeconds = epochSeconds;
        _eridLength = eridLength;
    }

    /// <summary>Our own current ERID for the epoch containing <paramref name="unixSeconds"/> —
    /// the address we present on the wire this window.</summary>
    public string MyErid(long unixSeconds)
        => EphemeralRoutingId.Derive(_myRoutingKey, unixSeconds, _epochSeconds, _eridLength);

    /// <summary>Store a peer's routingKey, learned inside an established session. Idempotent;
    /// a later call replaces an earlier key for the same peer (e.g. after a re-key).</summary>
    public void RememberPeer(string peerUhid, byte[] peerRoutingKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(peerRoutingKey);
        if (peerRoutingKey.Length == 0)
            throw new ArgumentException("peerRoutingKey cannot be empty.", nameof(peerRoutingKey));
        _peerKeys[peerUhid] = (byte[])peerRoutingKey.Clone();
    }

    /// <summary>Forget a peer (session torn down, or peer excommunicated). Returns false if unknown.</summary>
    public bool ForgetPeer(string peerUhid) => _peerKeys.TryRemove(peerUhid, out _);

    /// <summary>The current ERID a known peer presents this epoch, or null if we hold no key for them.</summary>
    public string? EridForPeer(string peerUhid, long unixSeconds)
        => _peerKeys.TryGetValue(peerUhid, out var key)
            ? EphemeralRoutingId.Derive(key, unixSeconds, _epochSeconds, _eridLength)
            : null;

    /// <summary>
    /// Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the given epoch,
    /// or null if no known peer currently presents it. O(n) over known peers — a node's actual
    /// relationship count (tens–hundreds); a caller on a hot path can cache the ERID→peer map per
    /// epoch (it only changes at a 15-minute boundary).
    /// </summary>
    public string? ResolvePeer(string erid, long unixSeconds)
    {
        if (string.IsNullOrEmpty(erid)) return null;
        foreach (var kvp in _peerKeys)
        {
            var candidate = EphemeralRoutingId.Derive(kvp.Value, unixSeconds, _epochSeconds, _eridLength);
            if (string.Equals(candidate, erid, StringComparison.Ordinal))
                return kvp.Key;
        }
        return null;
    }

    /// <summary>Number of peers whose routingKey we currently hold.</summary>
    public int KnownPeerCount => _peerKeys.Count;
}
