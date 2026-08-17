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

    /// <summary>True once a peer has completed the link handshake.</summary>
    bool IsLinked { get; }

    /// <summary>The linked peer's AetherTag, or null.</summary>
    string? PeerTag { get; }

    /// <summary>Bring the radio up and link to another phone running this app.</summary>
    void Link();

    /// <summary>Send raw bytes to the linked peer. Returns false if not linked / send failed.</summary>
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
