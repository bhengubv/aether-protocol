// SPDX-License-Identifier: MIT

import Foundation

/// The kind of WebRTC signalling message exchanged while a direct link is set up.
public enum WebRtcSignalType: Int, Sendable, Codable {
    /// SDP offer from the initiating peer.
    case offer = 0

    /// SDP answer from the responding peer.
    case answer = 1

    /// A trickled ICE candidate.
    case iceCandidate = 2
}

/// A single WebRTC signalling message — the SDP offer/answer or an ICE candidate two peers must
/// exchange before a direct data channel can open.
///
/// Carried by a ``WebRtcSignaling`` channel (e.g. over the AetherNet relay, the radio mesh, or an
/// SMS ignition link) — never a central signalling server.
public struct WebRtcSignal: Sendable, Codable, Equatable {
    /// UHID of the node that produced this signal.
    public let fromUhid: String

    /// UHID of the node this signal is addressed to.
    public let toUhid: String

    /// What this signal carries.
    public let type: WebRtcSignalType

    /// The SDP text — set for ``WebRtcSignalType/offer`` / ``WebRtcSignalType/answer``.
    public let sdp: String?

    /// The ICE candidate string — set for ``WebRtcSignalType/iceCandidate``.
    public let candidate: String?

    /// The SDP m-line index for the ICE candidate (0 for the single data section).
    public let sdpMLineIndex: UInt16

    /// The SDP mid for the ICE candidate.
    public let sdpMid: String?

    public init(
        fromUhid: String,
        toUhid: String,
        type: WebRtcSignalType,
        sdp: String? = nil,
        candidate: String? = nil,
        sdpMLineIndex: UInt16 = 0,
        sdpMid: String? = nil
    ) {
        self.fromUhid = fromUhid
        self.toUhid = toUhid
        self.type = type
        self.sdp = sdp
        self.candidate = candidate
        self.sdpMLineIndex = sdpMLineIndex
        self.sdpMid = sdpMid
    }
}
