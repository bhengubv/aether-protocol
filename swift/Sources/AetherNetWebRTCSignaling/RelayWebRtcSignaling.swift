// SPDX-License-Identifier: MIT

import Foundation

/// A byte-transport seam the ``RelayWebRtcSignaling`` carrier rides.
///
/// This is deliberately narrower than ``AetherNetProtocol/TransportService``: signalling only needs
/// to *send opaque bytes to a peer* and *be told when bytes arrive*. The core `TransportService`
/// protocol declares the send half but registers inbound bytes on the concrete type (e.g.
/// `InProcessTransport.onDataReceived`), not on the protocol — so this small protocol captures both
/// halves in one place, letting the carrier ride any channel (the gap-2 circuit relay, an in-process
/// pair, a QUIC/HTTP relay) that can provide them.
///
/// Mirrors the receive surface of the C# `ITransportService.DataReceived` event that
/// `RelayWebRtcSignaling` (C#) subscribes to.
public protocol SignalingTransport: AnyObject, Sendable {
    /// Sends `data` to `peerUhid`. Returns `true` if the bytes were handed to the channel.
    @discardableResult
    func sendAsync(peerUhid: String, data: Data) async -> Bool

    /// Registers the handler invoked for inbound bytes: `(fromUhid, data)`. Replacing it is allowed.
    func onDataReceived(_ handler: @escaping @Sendable (String, Data) -> Void)
}

/// Carries WebRTC SDP/ICE signalling over an existing ``SignalingTransport`` — typically the
/// AetherNet circuit relay, but the radio mesh works too — so two distant peers can negotiate a
/// direct data channel without a dedicated signalling server. Once the channel is open, the media
/// and app traffic flow peer-to-peer; only the short handshake ever touches the relay.
///
/// The Swift counterpart of C# `AetherNet.Transport.WebRtc.RelayWebRtcSignaling`. Each signal is
/// framed with a 4-byte magic prefix (`AWS1`) and a compact JSON body whose bytes are **identical**
/// to the C# frame (PascalCase keys in declaration order, integer `Type`, null members omitted) so a
/// Swift node and a C#/Go node can complete the handshake across languages. Inbound bytes on the
/// underlying transport that lack the prefix are ignored — they are ordinary application traffic, not
/// signalling.
///
/// Give this a transport whose inbound stream is dedicated to signalling (e.g. a relay connection
/// reserved for control traffic), so the prefixed control frames never reach the application data
/// path. It plugs straight into ``WebRtcTransportService`` via the ``WebRtcSignaling`` seam.
public final class RelayWebRtcSignaling: WebRtcSignaling, @unchecked Sendable {
    /// `AWS1` = **A**ether **W**ebRtc **S**ignal, framing v1. Must byte-match the C# `Magic`.
    static let magic: [UInt8] = [UInt8(ascii: "A"), UInt8(ascii: "W"), UInt8(ascii: "S"), UInt8(ascii: "1")]

    private let channel: any SignalingTransport
    private let lock = NSLock()
    private var handler: (@Sendable (WebRtcSignal) -> Void)?

    /// Wraps `channel`, subscribing to its inbound bytes for the lifetime of this carrier.
    public init(channel: any SignalingTransport) {
        self.channel = channel
        channel.onDataReceived { [weak self] fromUhid, data in
            self?.onChannelData(fromUhid: fromUhid, data: data)
        }
    }

    // MARK: - WebRtcSignaling

    @discardableResult
    public func send(peerUhid: String, signal: WebRtcSignal) async -> Bool {
        let frame = Self.frame(signal)
        return await channel.sendAsync(peerUhid: peerUhid, data: frame)
    }

    public func onSignal(_ handler: @escaping @Sendable (WebRtcSignal) -> Void) async {
        // `withLock` is the async-safe scoped form (the bare lock()/unlock() pair is unavailable from
        // async contexts under the Swift 6 language mode).
        lock.withLock { self.handler = handler }
    }

    // MARK: - Inbound

    private func onChannelData(fromUhid: String, data: Data) {
        guard Self.hasMagic(data) else { return } // ordinary app traffic, not a signalling frame
        let body = data.subdata(in: (data.startIndex + Self.magic.count) ..< data.endIndex)
        guard let signal = WebRtcSignal.fromRelayJson(body) else {
            // Malformed frame after a valid prefix — discard (ICE re-gathers on retry).
            return
        }
        lock.lock()
        let h = handler
        lock.unlock()
        h?(signal)
    }

    // MARK: - Framing (AWS1 + JSON), byte-identical to C# RelayWebRtcSignaling

    /// Frames `signal` as `AWS1` ++ JSON body.
    static func frame(_ signal: WebRtcSignal) -> Data {
        var out = Data(magic)
        out.append(signal.toRelayJson())
        return out
    }

    static func hasMagic(_ data: Data) -> Bool {
        guard data.count >= magic.count else { return false }
        let base = data.startIndex
        return data[base] == magic[0]
            && data[base + 1] == magic[1]
            && data[base + 2] == magic[2]
            && data[base + 3] == magic[3]
    }
}

// MARK: - Relay JSON body (byte-identical to C# System.Text.Json source-gen output)

extension WebRtcSignal {
    /// Serialises the frame body to UTF-8 JSON **byte-identical** to the C# `WebRtcSignal`
    /// serialised by `WebRtcSignalJsonContext` (System.Text.Json, source-generated):
    ///
    /// - PascalCase keys, emitted in the C# **declaration order**
    ///   `FromUhid, ToUhid, Type, Sdp, Candidate, SdpMLineIndex, SdpMid`;
    /// - `Type` as its **integer** value (no string-enum converter is configured);
    /// - `SdpMLineIndex` (a value type) is **always** written, even when 0;
    /// - `Sdp` / `Candidate` / `SdpMid` are **omitted** when nil (`WhenWritingNull`);
    /// - no insignificant whitespace.
    ///
    /// Hand-built in exact field order — the ``AetherNetProtocol/ForgeAnnounceService`` house style —
    /// so cross-language byte-identity does not depend on `JSONEncoder` key ordering.
    func toRelayJson() -> Data {
        var json = "{"
        json += "\"FromUhid\":\(Self.jsonString(fromUhid)),"
        json += "\"ToUhid\":\(Self.jsonString(toUhid)),"
        json += "\"Type\":\(type.rawValue)"
        if let sdp { json += ",\"Sdp\":\(Self.jsonString(sdp))" }
        if let candidate { json += ",\"Candidate\":\(Self.jsonString(candidate))" }
        json += ",\"SdpMLineIndex\":\(sdpMLineIndex)"
        if let sdpMid { json += ",\"SdpMid\":\(Self.jsonString(sdpMid))" }
        json += "}"
        return Data(json.utf8)
    }

    /// Parses a relay-framed JSON body. Order-independent and tolerant of extra/missing members,
    /// matching the C# JSON reader. Returns nil on malformed input.
    static func fromRelayJson(_ body: Data) -> WebRtcSignal? {
        try? JSONDecoder().decode(RelayWire.self, from: body).toSignal()
    }

    /// Encodes a JSON string literal (including the surrounding quotes) **byte-identical** to
    /// `System.Text.Json`'s default `JavaScriptEncoder.Default`.
    ///
    /// STJ escapes, beyond the JSON-mandated set:
    ///  - `"` as `"` (NOT `\"`),
    ///  - `& ' + < > \`` as `\uXXXX`,
    ///  - every non-ASCII code point (> 0x7E) as `\uXXXX` (surrogate pairs emitted as two `\uXXXX`),
    /// all with UPPERCASE hex. C0 control characters use the short escapes `\b \t \n \f \r` where
    /// defined, else `\uXXXX`. Backslash is `\\`. `/` stays literal.
    ///
    /// Iterates the **UTF-16 view** so it matches STJ's per-`char` behaviour exactly: astral scalars
    /// arrive as their two surrogate code units and each emits its own `\uXXXX`.
    private static func jsonString(_ s: String) -> String {
        var out = "\""
        for u in s.utf16 {
            switch u {
            case 0x08: out += "\\b"
            case 0x09: out += "\\t"
            case 0x0A: out += "\\n"
            case 0x0C: out += "\\f"
            case 0x0D: out += "\\r"
            case 0x5C: out += "\\\\" // backslash
            default:
                // Printable ASCII that STJ leaves literal: 0x20..0x7E minus the escaped punctuation.
                if u >= 0x20, u <= 0x7E, !stjEscapeAscii.contains(u) {
                    out.unicodeScalars.append(Unicode.Scalar(u)!)
                } else {
                    out += "\\u" + String(u, radix: 16, uppercase: true).leftPadded(to: 4)
                }
            }
        }
        out += "\""
        return out
    }

    /// ASCII code units (0x20–0x7E) that `System.Text.Json`'s default encoder escapes as `\uXXXX`
    /// even though plain JSON would not: `" & ' + < > \``.
    private static let stjEscapeAscii: Set<UInt16> = [
        0x22, // "
        0x26, // &
        0x27, // '
        0x2B, // +
        0x3C, // <
        0x3E, // >
        0x60, // `
    ]

    /// PascalCase-keyed `Codable` shadow used only for tolerant *decoding* of the relay body.
    /// `CodingKeys` carry the PascalCase JSON names so the Swift member names can stay idiomatic
    /// (and `type` avoids the reserved `Type` metatype clash).
    private struct RelayWire: Codable {
        let fromUhid: String
        let toUhid: String
        let type: Int
        let sdp: String?
        let candidate: String?
        let sdpMLineIndex: UInt16?
        let sdpMid: String?

        private enum CodingKeys: String, CodingKey {
            case fromUhid = "FromUhid"
            case toUhid = "ToUhid"
            case type = "Type"
            case sdp = "Sdp"
            case candidate = "Candidate"
            case sdpMLineIndex = "SdpMLineIndex"
            case sdpMid = "SdpMid"
        }

        func toSignal() -> WebRtcSignal? {
            guard let kind = WebRtcSignalType(rawValue: type) else { return nil }
            return WebRtcSignal(
                fromUhid: fromUhid,
                toUhid: toUhid,
                type: kind,
                sdp: sdp,
                candidate: candidate,
                sdpMLineIndex: sdpMLineIndex ?? 0,
                sdpMid: sdpMid)
        }
    }
}

private extension String {
    /// Left-pads with `'0'` to `width` characters (used for the fixed 4-digit `\uXXXX` hex). Returns
    /// self unchanged when already at/over `width`.
    func leftPadded(to width: Int) -> String {
        count >= width ? self : String(repeating: "0", count: width - count) + self
    }
}
