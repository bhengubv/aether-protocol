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
    /// Whether <see cref="Link"/> reaches OUT to another phone rather than just listening.
    ///
    /// <para>
    /// BLE, NFC and LoRa are passive: bringing them up advertises and listens, and two phones doing it
    /// at once is exactly how they find each other. Wi-Fi Direct is not — it calls <c>connect()</c>,
    /// and two phones calling it at each other is a race. Losing that race is not quiet: Android falls
    /// back to an "Invitation to connect" dialog on the other handset that nobody is looking at, and
    /// which takes window focus so the app looks wedged too.
    /// </para>
    ///
    /// <para>
    /// So a radio that initiates is never brought up as a bystander. Only the thing that knows who
    /// should host — <c>WifiDirectBroker</c>, which decides that from the two tags and hands the
    /// credentials over the link that already works — may start it. Watched on the P30 2026-08-20:
    /// tapping Connect on both phones raised exactly that dialog and stalled the test behind it.
    /// </para>
    /// </summary>
    bool Initiates => false;

    /// <summary>True once a peer has completed the link handshake.</summary>
    bool IsLinked { get; }

    /// <summary>The linked peer's AetherTag, or null.</summary>
    string? PeerTag { get; }

    /// <summary>Bring the radio up and link to another phone running this app.</summary>
    void Link();

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
