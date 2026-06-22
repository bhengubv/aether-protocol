// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.WebRtc;

/// <summary>The kind of WebRTC signalling message exchanged while a direct link is set up.</summary>
public enum WebRtcSignalType
{
    /// <summary>SDP offer from the initiating peer.</summary>
    Offer = 0,

    /// <summary>SDP answer from the responding peer.</summary>
    Answer = 1,

    /// <summary>A trickled ICE candidate.</summary>
    IceCandidate = 2,
}

/// <summary>
/// A single WebRTC signalling message — the SDP offer/answer or an ICE candidate two peers must
/// exchange before a direct <c>RTCDataChannel</c> can open.
///
/// <para>Carried by an <see cref="IWebRtcSignaling"/> channel (e.g. over the AetherNet QUIC/HTTP
/// relay, the radio mesh, or an SMS ignition link) — never a central signalling server.</para>
/// </summary>
public sealed record WebRtcSignal
{
    /// <summary>UHID of the node that produced this signal.</summary>
    public required string FromUhid { get; init; }

    /// <summary>UHID of the node this signal is addressed to.</summary>
    public required string ToUhid { get; init; }

    /// <summary>What this signal carries.</summary>
    public required WebRtcSignalType Type { get; init; }

    /// <summary>The SDP text — set for <see cref="WebRtcSignalType.Offer"/> / <see cref="WebRtcSignalType.Answer"/>.</summary>
    public string? Sdp { get; init; }

    /// <summary>The ICE candidate string — set for <see cref="WebRtcSignalType.IceCandidate"/>.</summary>
    public string? Candidate { get; init; }

    /// <summary>The SDP m-line index for the ICE candidate (0 for the single data section).</summary>
    public ushort SdpMLineIndex { get; init; }

    /// <summary>The SDP mid for the ICE candidate.</summary>
    public string? SdpMid { get; init; }
}
