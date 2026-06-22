// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// Carries WebRTC SDP/ICE signalling between two peers by UHID, so a direct data channel can be
/// negotiated without a central signalling server.
///
/// <para>Any already-reachable channel can back this — the AetherNet QUIC/HTTP relay, the radio
/// mesh, or (for cold first contact between distant peers) an SMS ignition link. The implementation
/// frames signals so the underlying channel only ever forwards opaque bytes.</para>
/// </summary>
public interface IWebRtcSignaling
{
    /// <summary>Delivers a signalling message to <paramref name="peerUhid"/>.</summary>
    /// <returns>True if the signal was handed to the underlying channel; false otherwise.</returns>
    Task<bool> SendAsync(string peerUhid, WebRtcSignal signal, CancellationToken cancellationToken = default);

    /// <summary>Raised when a signalling message addressed to the local node arrives.</summary>
    event Action<WebRtcSignal>? SignalReceived;
}
