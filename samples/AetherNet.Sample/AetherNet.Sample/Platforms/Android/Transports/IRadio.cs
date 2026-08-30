// SPDX-License-Identifier: MIT
#if ANDROID
namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// One physical radio inside the app (Wi-Fi Direct, BLE, NFC, NearLink, LoRa). Every radio
/// exposes the same tiny surface so the UI can list them, link over the chosen one, and move
/// bytes — all inside the single APK, no second package.
/// </summary>
internal interface IRadio
{
    /// <summary>Human-readable radio name shown in the picker.</summary>
    string Name { get; }

    /// <summary>Whether this radio exists / is usable on the current device.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Why this radio cannot be used here — missing hardware, a switched-off adapter — or null when
    /// it is usable. A radio the phone does not physically have must say so rather than appear ready.
    /// </summary>
    string? UnavailableReason => null;

    /// <summary>
    /// Can the person holding the phone do something about <see cref="UnavailableReason"/>?
    ///
    /// <para>
    /// A missing permission or a switched-off adapter is fixable — offer the tap. Absent silicon is
    /// not, and inviting someone to fix a phone that has no NFC chip in it is just a lie with a
    /// friendly tone.
    /// </para>
    /// </summary>
    bool IsFixable => false;

    /// <summary>
    /// Roughly what this radio can carry, in bits per second.
    ///
    /// <para>
    /// Used to pick which linked radio moves the traffic. Be honest here rather than optimistic: this
    /// decides whether a call is placed over a link that can carry it.
    /// </para>
    ///
    /// <para>
    /// <b>Measure it. Do not calculate it.</b> This number has been wrong twice, and each time the
    /// wrong one was repeated as fact for hours. 5 kbps came from a link still on the 23-byte default
    /// MTU. 100 kbps replaced it by arithmetic — MTU 517 times fifty a second — and was written down
    /// as "measured" by someone who had not measured it. Counted during live calls, BLE between these
    /// handsets moves about <b>11 kbps in one direction</b>: a GATT connection carries one operation
    /// in flight at a time, so MTU changes the bytes per operation and not the operations per second.
    /// </para>
    ///
    /// <para>
    /// PROTOCOL_SPEC §5.5 holds what has actually been counted, per radio. A voice call needs 50
    /// packets/sec each way; BLE fails that by five times and Wi-Fi Direct clears it. If audio is not
    /// getting through, read that section before blaming the code.
    /// </para>
    /// </summary>
    long MaxBandwidthBps => 0;

    /// <summary>
    /// What this radio is actually doing, measured — as opposed to <see cref="MaxBandwidthBps"/>,
    /// which is what it says about itself.
    /// </summary>
    /// <remarks>
    /// Radios that carry nothing do not need one; the default is a meter that has never been fed and
    /// therefore reports nothing, which is the honest answer for a radio with no traffic.
    /// </remarks>
    AetherNet.Sample.Shared.Services.LinkQuality Quality => AetherNet.Sample.Shared.Services.LinkQuality.Silent;

    /// <summary>True once a peer has completed the link handshake.</summary>
    bool IsLinked { get; }

    /// <summary>The linked peer's AetherTag, or null.</summary>
    string? PeerTag { get; }

    /// <summary>
    /// Every peer currently linked over this radio, by wire address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relay node is linked to several people at once — that is what makes it a relay — so
    /// <see cref="PeerTag"/> is not enough to describe it. The default answers with the one peer a
    /// single-peer radio has, so a radio that can only ever hold one link needs to say nothing.
    /// </para>
    /// </remarks>
    IReadOnlyCollection<string> Peers => PeerTag is { } only ? new[] { only } : [];

    /// <summary>
    /// Send to one named peer rather than to whoever happens to be first.
    /// </summary>
    /// <remarks>
    /// Carrying somebody else's traffic means choosing where it goes. Without this a node with six
    /// links passes everything to the same one — which is not a relay, it is a phone shouting into
    /// the nearest socket.
    /// </remarks>
    System.Threading.Tasks.Task<bool> SendToAsync(string peerAddress, byte[] data,
        AetherNet.Sample.Shared.Services.SendLane lane)
        => string.Equals(peerAddress, PeerTag, StringComparison.Ordinal)
            ? SendAsync(data, lane)
            : System.Threading.Tasks.Task.FromResult(false);

    /// <summary>Bring the radio up and link to another phone running this app.</summary>
    void Link();

    /// <summary>
    /// Bring the radio up to meet one particular person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every radio had invented its own answer to the same three questions — who starts, how do we
    /// find each other, which channel do we agree on. Wi-Fi Direct compared AetherTags and derived a
    /// group from a public key. BLE picked its roles from what the silicon could do and never looked
    /// at a tag at all. Three answers to one question, none of them shared, and the only tag-aware one
    /// bolted to a single radio — so a phone without that radio could not meet anybody.
    /// </para>
    /// <para>
    /// The question is answered once, above every radio, and handed down. What a radio does with it is
    /// its own business: Wi-Fi Direct makes it a group name, Wi-Fi a multicast rendezvous and a port,
    /// LoRa an address inside a shared channel, NFC ignores it because the tap is the meeting.
    /// </para>
    /// <para>
    /// The default is the old behaviour, so a radio that has not been taught this yet still comes up
    /// and still carries traffic — it simply comes up for everybody rather than for somebody.
    /// </para>
    /// </remarks>
    void Link(AetherNet.Sample.Shared.Services.Meeting meeting) => Link();

    /// <summary>
    /// Send raw bytes to the linked peer.
    ///
    /// <para>
    /// <b>Returns false whenever the bytes will definitely not reach the peer</b> — nothing linked, no
    /// peer to address, the write threw, or this device has no such radio. A radio the phone does not
    /// have must return false; it must never report success for bytes it silently discarded, because
    /// the layer above uses this to decide whether a call is ringing anywhere at all.
    /// </para>
    ///
    /// <para>
    /// <b>True means this radio has accepted responsibility for delivery, not that it arrived.</b> A
    /// queue-based radio like BLE returns true once the frame is queued and cannot honestly say more;
    /// NFC waits on a physical tap and returns false until there is one. So the caller must never read
    /// true as proof the far end heard anything — that is what the ring timeout in CallService is for,
    /// and why it is deliberately independent of every radio.
    /// </para>
    /// </summary>
    System.Threading.Tasks.Task<bool> SendAsync(byte[] data);

    /// <summary>
    /// Send in a particular lane, so speech is never queued behind a file.
    /// </summary>
    /// <remarks>
    /// Radios that cannot separate lanes ignore it and send in order, which is what they did before —
    /// no worse than they were, and correct for a radio with one queue in hardware.
    /// </remarks>
    System.Threading.Tasks.Task<bool> SendAsync(byte[] data, AetherNet.Sample.Shared.Services.SendLane lane)
        => SendAsync(data);

    /// <summary>Tear the radio down.</summary>
    void Stop();

    /// <summary>Raised with the peer's AetherTag once linked.</summary>
    event System.Action<string>? PeerLinked;

    /// <summary>Raised with (peerTag, rawBytes) when a packet arrives.</summary>
    event System.Action<string, byte[]>? DataReceived;

    /// <summary>Raised with a human-readable status line for the radio log.</summary>
    event System.Action<string>? Status;
}
#endif
