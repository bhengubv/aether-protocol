// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which phone in your Circle is currently carrying traffic for the others, and where to reach it.
///
/// <para>
/// This is the piece that keeps the second leg decentralised. A relay needs an address, and the
/// obvious place to get one is a directory somebody runs — which is the central API this whole
/// network exists not to have. So the address comes from the person: a contact turns on the gateway,
/// their phone tells the people it already has sessions with, and those phones point at it.
/// </para>
///
/// <para>
/// Nothing is discovered, nothing is published, and nobody outside a Circle learns anything. A proxy
/// is a favour between people who already know each other, and it ends when they stop offering.
/// </para>
/// </summary>
public sealed class ProxyDirectory
{
    /// <summary>Where this phone stores the fact that it is offering to relay, across restarts.</summary>
    public const string GatewayEnabledKey = "gateway_enabled";

    private readonly ConcurrentDictionary<string, string> _offers = new(StringComparer.Ordinal);
    private readonly AetherStore _store;

    public ProxyDirectory(AetherStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised when somebody starts or stops offering, so the radio can re-link itself.</summary>
    public event Action? Changed;

    /// <summary>Is this phone offering to carry traffic for the others?</summary>
    public bool IsGateway
    {
        get => _store.GetFlag(GatewayEnabledKey);
        set
        {
            if (IsGateway == value) return;
            _store.SetFlag(GatewayEnabledKey, value);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The proxy to use right now, or null when nobody is offering.
    /// </summary>
    /// <remarks>
    /// Ordered rather than "whichever arrived last", so two phones in the same Circle pick the same
    /// proxy and end up able to reach each other — picking differently would leave both connected and
    /// neither reachable.
    /// </remarks>
    public string? Best => _offers.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Value).FirstOrDefault();

    /// <summary>Everyone who might be on the far side of the relay: the contacts we hold sessions with.</summary>
    public IReadOnlyCollection<string> Reachable =>
        _store.GetContacts().Where(c => c.IsMutual).Select(c => c.Tag).ToArray();

    /// <summary>A contact is offering to relay, at this address.</summary>
    public void Offer(string peerTag, string url)
    {
        if (string.IsNullOrEmpty(peerTag) || string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme is not ("http" or "https")) return;

        if (_offers.TryGetValue(peerTag, out var existing) && string.Equals(existing, url, StringComparison.Ordinal))
            return;

        _offers[peerTag] = url;
        Changed?.Invoke();
    }

    /// <summary>A contact has stopped offering, or has been removed.</summary>
    public void Withdraw(string peerTag)
    {
        if (string.IsNullOrEmpty(peerTag)) return;
        if (_offers.TryRemove(peerTag, out _)) Changed?.Invoke();
    }

    /// <summary>How many people in your Circle are offering to carry traffic.</summary>
    public int OfferCount => _offers.Count;
}
