// SPDX-License-Identifier: MIT

import Foundation
import CDataChannel
import AetherNetProtocol
import AetherNetWebRTCSignaling

/// Direct peer-to-peer transport over a WebRTC data channel (libdatachannel, portable C/C++).
///
/// NAT traversal is handled by ICE/STUN, with WebRTC's own TURN as last resort. The initial SDP/ICE
/// handshake is carried by an injected ``WebRtcSignaling`` channel (e.g. the AetherNet relay), so no
/// central signalling server is required. Conforms to `TransportService` so `rankTransports` slots
/// it between the radio mesh (cheap, proximity) and the QUIC/HTTP relay (last resort): a direct
/// internet path is used when one can be negotiated, otherwise the relay carries the traffic.
public final class WebRtcTransportService: TransportService, @unchecked Sendable {
    private static let connectTimeout: TimeInterval = 20

    /// Serverless default: NO ICE servers, so a node never contacts a STUN/TURN server. Direct
    /// links form on the same LAN or when a peer has a public address; for NAT traversal without a
    /// server, route through the circuit-relay-v2 transport (peers relay for peers). Opt into
    /// STUN/TURN by passing an explicit list.
    public static let defaultIceServers: [String] = []

    private let localUhid: String
    private let signaling: any WebRtcSignaling
    private let iceServers: [String]
    private let _metrics = PerTransportMetrics()

    private let lock = NSLock()
    private var peers: [String: WebRtcPeerLink] = [:]
    private var onData: (@Sendable (String, Data) -> Void)?
    private var disposed = false

    /// Creates the transport for `localUhid`, routing signalling through `signaling`.
    ///
    /// - Parameter iceServers: `nil` uses the serverless default of NO ICE servers
    ///   (host-candidate-only ICE; never contacts a STUN/TURN server, links form on the same LAN or
    ///   when a peer has a public address). For NAT traversal without a server, route through the
    ///   circuit-relay-v2 transport (peers relay for peers). An explicit list is respected verbatim,
    ///   so pass one to opt into STUN/TURN, or an empty list to keep host-candidate-only ICE
    ///   (same-LAN / tests). URLs are in libdatachannel form, e.g. `stun:host:port`,
    ///   `turn:user:pass@host:port`.
    public init(
        localUhid: String,
        signaling: any WebRtcSignaling,
        iceServers: [String]? = nil
    ) {
        precondition(!localUhid.isEmpty, "localUhid is required")
        self.localUhid = localUhid
        self.signaling = signaling
        self.iceServers = iceServers ?? Self.defaultIceServers

        let weakSelf = WeakBox(self)
        Task {
            await signaling.onSignal { signal in
                weakSelf.value?.handleSignal(signal)
            }
        }
    }

    /// Registers the handler for inbound bytes — the receive surface, mirroring
    /// `InProcessTransport.onDataReceived`.
    public func onDataReceived(_ handler: @escaping @Sendable (String, Data) -> Void) {
        lock.lock(); defer { lock.unlock() }
        onData = handler
    }

    // MARK: - TransportService metadata

    public var name: String { "WebRTC P2P" }

    public var isAvailable: Bool {
        lock.lock(); defer { lock.unlock() }
        return !disposed
    }

    public var maxBandwidthBps: Int64 { 100_000_000 }   // direct link — bounded by the local NIC
    public var maxRangeMeters: Int32 { 0 }              // internet — unbounded
    public var powerCostRelative: Int32 { 5 }          // between radio mesh (low) and relay (high)
    public var maxConcurrentPeers: Int32 { 256 }
    public var metrics: PerTransportMetrics? { _metrics }

    // MARK: - TransportService send / status

    public func sendAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool {
        lock.lock()
        let isDisposed = disposed
        lock.unlock()
        if isDisposed || peerUhid.isEmpty { return false }

        guard let link = await getOrCreateLink(peerUhid: peerUhid, asInitiator: true) else {
            return false
        }
        let start = DispatchTime.now()
        let ok = await link.send(data, openTimeout: Self.connectTimeout)
        let elapsedMs = Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1e6
        _metrics.recordSample(rttMs: elapsedMs, success: ok, bytesTransferred: ok ? data.count : 0)
        return ok
    }

    public func sendStreamAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool {
        await sendAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken)
    }

    public func isConnected(peerUhid: String) -> Bool {
        lock.lock()
        let link = peers[peerUhid]
        lock.unlock()
        return link?.isOpen ?? false
    }

    /// Tears down all peer connections and stops accepting new ones.
    public func close() {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let links = Array(peers.values)
        peers.removeAll()
        onData = nil
        lock.unlock()
        for link in links { link.close() }
    }

    // MARK: - Signalling inbound

    private func handleSignal(_ signal: WebRtcSignal) {
        lock.lock()
        let isDisposed = disposed
        lock.unlock()
        if isDisposed || signal.toUhid != localUhid { return }

        switch signal.type {
        case .offer:
            Task {
                if let link = await getOrCreateLink(peerUhid: signal.fromUhid, asInitiator: false),
                   let sdp = signal.sdp {
                    link.acceptOffer(sdp: sdp)
                }
            }
        case .answer:
            lock.lock()
            let link = peers[signal.fromUhid]
            lock.unlock()
            if let link, let sdp = signal.sdp {
                link.acceptAnswer(sdp: sdp)
            }
        case .iceCandidate:
            lock.lock()
            let link = peers[signal.fromUhid]
            lock.unlock()
            link?.addRemoteCandidate(signal)
        }
    }

    private func getOrCreateLink(peerUhid: String, asInitiator: Bool) async -> WebRtcPeerLink? {
        lock.lock()
        if disposed {
            lock.unlock()
            return nil
        }
        if let existing = peers[peerUhid], !existing.isClosed {
            lock.unlock()
            if asInitiator {
                _ = await existing.waitOpen(timeout: Self.connectTimeout)
            }
            return existing
        }

        let onData = self.onData
        let link = WebRtcPeerLink(
            localUhid: localUhid,
            peerUhid: peerUhid,
            iceServers: iceServers,
            signaling: signaling,
            onData: { peer, data in onData?(peer, data) })
        peers[peerUhid] = link
        lock.unlock()

        link.registerOnClosed { [weak self] in
            guard let self else { return }
            self.lock.lock()
            self.peers.removeValue(forKey: peerUhid)
            self.lock.unlock()
        }
        link.start(asInitiator: asInitiator)

        if asInitiator {
            _ = await link.waitOpen(timeout: Self.connectTimeout)
        }
        return link
    }
}

// MARK: - WeakBox

/// A `Sendable` weak reference holder, so a C/async callback can reach back to the transport without
/// extending its lifetime.
private final class WeakBox<T: AnyObject>: @unchecked Sendable {
    weak var value: T?
    init(_ value: T) { self.value = value }
}
