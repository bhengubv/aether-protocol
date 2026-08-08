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
