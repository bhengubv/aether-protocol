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
    /// A note on BLE, because an earlier version of this comment had it wrong and the wrong number was
    /// then repeated as fact for hours. These handsets negotiate an ATT MTU of 517 and carry 512-byte
    /// packets at about fifty a second — on the order of 100 kbps, comfortably more than the 24 kbps a
    /// voice call needs. The "5 kbps" figure came from a link still on the 23-byte default MTU. If
    /// audio is not getting through, measure before blaming the radio.
    /// </para>
    /// </summary>
    long MaxBandwidthBps => 0;

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
