// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.Services;

/// <summary>
/// One radio, as far as choosing between them goes.
/// </summary>
/// <param name="Name">What it is called.</param>
/// <param name="IsLinked">Whether it is holding a link right now.</param>
/// <param name="MeasuredBps">What has actually crossed it, or 0 when too little has to mean anything.</param>
/// <param name="AdvertisedBps">What it says about itself.</param>
public readonly record struct RadioSpeed(string Name, bool IsLinked, long MeasuredBps, long AdvertisedBps)
{
    /// <summary>
    /// What this radio can be counted on to carry.
    /// </summary>
    /// <remarks>
    /// Measured first, because every advertised figure in this app has been wrong: BLE published
    /// 2 Mbps and delivered 11 kbps in one direction, and Wi-Fi Direct still reports a flat 250 Mbps
    /// that nothing has checked. The measured number is a floor — what has crossed, not what could —
    /// so it is only trusted once there is one, and until then the guess is all there is.
    /// </remarks>
    public long Carries => MeasuredBps > 0 ? MeasuredBps : AdvertisedBps;

    /// <summary>
    /// The transport-capability tag for this radio (see <see cref="TransportCapability"/>), or null
    /// when the radio maps to no advertised transport.
    /// </summary>
    /// <remarks>
    /// Set only when the caller wants the choice to respect what the PEER can carry. A radio the peer
    /// has no matching transport for cannot deliver to it however fast it is locally, so this is what
    /// lets <see cref="RadioChoice.Order(IEnumerable{RadioSpeed}, string?, IReadOnlySet{string}?)"/>
    /// prefer a transport both ends actually have. Left null on the peer-agnostic path, which then
    /// behaves exactly as it always did.
    /// </remarks>
    public string? Transport { get; init; }
}

/// <summary>
/// Which radio carries the traffic.
///
/// <para>
/// <b>Nobody is asked.</b> The person holding the phone picked a contact, not a transport. Putting
/// "Connect over: Wi-Fi Direct / Wi-Fi Aware / Internet / NFC / LoRa" in front of them, with a note
/// about mid-range chipsets, is handing them the plumbing and calling it a feature. Every radio tries
/// at once and the best one that got through carries, silently, and hands over when a better one
/// appears.
/// </para>
///
/// <para>
/// <b>Best, not first.</b> LoRa may connect before anything else and moves a few hundred bits a
/// second; Wi-Fi Direct arrives ten seconds later and carries a voice call. First-through would have
/// left the conversation on the wrong one.
/// </para>
/// </summary>
public static class RadioChoice
{
    /// <summary>
    /// How much wider a challenger must be before the traffic moves to it.
    /// </summary>
    /// <remarks>
    /// Two radios reporting nearly the same number is normal — the measured figure moves with every
    /// packet. Without a margin the traffic ping-pongs between them mid-call, re-handshaking each
    /// time, which reads to the person as a bad line and is really two radios being polite at each
    /// other. A quarter again is far outside that noise and far inside the gap between real radios,
    /// where the differences are hundredfold.
    /// </remarks>
    public const double Wider = 1.25;

    /// <summary>
    /// The radios worth trying, best first.
    /// </summary>
    /// <param name="radios">Every radio on the phone.</param>
    /// <param name="carrying">
    ///   Which one is carrying now, if any. It keeps the traffic unless something is clearly wider —
    ///   see <see cref="Wider"/>.
    /// </param>
    /// <remarks>
    /// Everything linked is returned, not just the winner, so a send that fails on the best radio
    /// falls straight down the list rather than failing outright — and a call in progress does not
    /// drop dead the moment its radio does.
    /// </remarks>
    public static IReadOnlyList<RadioSpeed> Order(IEnumerable<RadioSpeed> radios, string? carrying = null)
    {
        var linked = radios.Where(r => r.IsLinked).OrderByDescending(r => r.Carries).ToList();
        if (linked.Count == 0) return [];

        // The one already carrying stays in front unless something is properly wider. Sorting purely
        // by speed would move the traffic on a rounding difference.
        if (carrying is { Length: > 0 }
            && linked.FirstOrDefault(r => string.Equals(r.Name, carrying, StringComparison.Ordinal))
                is { Name.Length: > 0 } holder
            && linked[0].Carries < holder.Carries * Wider)
        {
            linked.Remove(holder);
            linked.Insert(0, holder);
        }

        return linked;
    }

    /// <summary>
    /// The radios worth trying, best first, respecting what the PEER can also carry.
    /// </summary>
    /// <param name="radios">Every radio on the phone, each tagged with its <see cref="RadioSpeed.Transport"/>.</param>
    /// <param name="carrying">Which one is carrying now, if any — see <see cref="Wider"/>.</param>
    /// <param name="peerTransports">
    ///   The transports the peer can carry, negotiated by the capability handshake — already the
    ///   intersection of the two phones' transports, so every tag in here is one BOTH ends have. Null
    ///   or empty means nothing has been negotiated yet, in which case this falls back to the
    ///   peer-agnostic <see cref="Order(IEnumerable{RadioSpeed}, string?)"/> and behaves exactly as
    ///   before.
    /// </param>
    /// <remarks>
    /// <para>
    /// A radio measured fast locally is useless if the peer has no matching transport to receive on —
    /// a phone with Wi-Fi Aware talking to one without it must not sit preferring Aware. So a radio
    /// whose transport the peer also carries leads, ordered among itself by <see cref="RadioSpeed.Carries"/>;
    /// radios the peer cannot carry follow, never dropped, so a link that is all there is still
    /// carries — reachability over tidiness.
    /// </para>
    /// <para>
    /// Hysteresis (<see cref="Wider"/>) still stops two near-equal radios ping-ponging, but only
    /// within the leading group: moving UP from a transport the peer lacks onto one it has is a
    /// deliberate optimisation, not speed noise, and is never held back.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RadioSpeed> Order(
        IEnumerable<RadioSpeed> radios, string? carrying, IReadOnlySet<string>? peerTransports)
    {
        if (peerTransports is null || peerTransports.Count == 0)
            return Order(radios, carrying);

        var linked = radios.Where(r => r.IsLinked).ToList();
        if (linked.Count == 0) return [];

        bool Mutual(RadioSpeed r) => r.Transport is { Length: > 0 } t && peerTransports.Contains(t);

        var preferred = linked.Where(Mutual).OrderByDescending(r => r.Carries);
        var fallback = linked.Where(r => !Mutual(r)).OrderByDescending(r => r.Carries);
        var ordered = preferred.Concat(fallback).ToList();

        // Keep the traffic where it is on a rounding difference — but only when the current holder is
        // in the same group as the new leader. A holder on a transport the peer lacks must yield to one
        // the peer has, however small the measured gap, because the fast link the holder shows is a link
        // the peer cannot hear.
        if (carrying is { Length: > 0 }
            && ordered.FirstOrDefault(r => string.Equals(r.Name, carrying, StringComparison.Ordinal))
                is { Name.Length: > 0 } holder
            && Mutual(holder) == Mutual(ordered[0])
            && ordered[0].Carries < holder.Carries * Wider)
        {
            ordered.Remove(holder);
            ordered.Insert(0, holder);
        }

        return ordered;
    }

    /// <summary>The one that should be carrying, or nothing when no radio is linked.</summary>
    public static RadioSpeed? Best(IEnumerable<RadioSpeed> radios, string? carrying = null) =>
        Order(radios, carrying) is [var best, ..] ? best : null;

    /// <summary>
    /// The one that should be carrying to this peer, or nothing when no radio is linked — the
    /// capability-aware companion to <see cref="Best(IEnumerable{RadioSpeed}, string?)"/>.
    /// </summary>
    public static RadioSpeed? Best(
        IEnumerable<RadioSpeed> radios, string? carrying, IReadOnlySet<string>? peerTransports) =>
        Order(radios, carrying, peerTransports) is [var best, ..] ? best : null;
}
