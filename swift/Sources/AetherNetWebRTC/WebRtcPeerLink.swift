// SPDX-License-Identifier: MIT

import Foundation
import CDataChannel

/// One WebRTC connection to a single peer: a libdatachannel `RTCPeerConnection` (`pc` handle) plus
/// its `RTCDataChannel` (`dc` handle), driving the offer/answer/ICE handshake over a
/// ``WebRtcSignaling`` channel and surfacing received bytes.
///
/// libdatachannel invokes its callbacks on an internal thread and hands back the `void *` user
/// pointer registered with `rtcSetUserPointer`. Each C trampoline recovers this instance from that
/// pointer (unretained — the link's lifetime is owned by ``WebRtcTransportService``) and forwards to
/// an instance method. All mutable state is guarded by an `NSLock`.
final class WebRtcPeerLink: @unchecked Sendable {
    private static let dataChannelLabel = "aether"

    private let localUhid: String
    private let peerUhid: String
    private let signaling: any WebRtcSignaling
    private let onData: @Sendable (String, Data) -> Void

    private let lock = NSLock()
    private var pc: Int32 = -1
    private var dc: Int32 = -1
    private var closed = false

    /// Fulfilled once when the data channel opens (`true`) or the link fails/closes first (`false`).
    private let openSignal = OneShot<Bool>()

    /// Invoked once when this link transitions to a terminal (closed/failed) state.
    private var onClosed: (@Sendable () -> Void)?

    var isOpen: Bool {
        lock.lock()
        let channel = dc
        let isClosed = closed
        lock.unlock()
        // rtcIsOpen reports the data channel's open state directly (no rtcGetState in the C API).
        return channel >= 0 && !isClosed && rtcIsOpen(channel)
    }

    var isClosed: Bool {
        lock.lock(); defer { lock.unlock() }
        return closed
    }

    /// Creates the link and its underlying `RTCPeerConnection`. `iceServers` are STUN/TURN URLs in
    /// libdatachannel form (e.g. `stun:stun.l.google.com:19302`); an empty list forces
    /// host-candidate-only ICE (no network dependency).
    init(
        localUhid: String,
        peerUhid: String,
        iceServers: [String],
        signaling: any WebRtcSignaling,
        onData: @escaping @Sendable (String, Data) -> Void
    ) {
        self.localUhid = localUhid
        self.peerUhid = peerUhid
        self.signaling = signaling
        self.onData = onData

        self.pc = Self.createPeerConnection(iceServers: iceServers)

        // Register the self-pointer, then wire the peer-connection callbacks.
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        rtcSetUserPointer(pc, selfPtr)
        rtcSetLocalDescriptionCallback(pc, Self.onLocalDescription)
        rtcSetLocalCandidateCallback(pc, Self.onLocalCandidate)
        rtcSetStateChangeCallback(pc, Self.onStateChange)
        // The responder receives its data channel through this callback.
        rtcSetDataChannelCallback(pc, Self.onDataChannel)
    }

    /// Begins the handshake. The initiator creates the data channel; with auto-negotiation enabled,
    /// libdatachannel then produces the offer and fires the local-description callback.
    func start(asInitiator: Bool) {
        guard asInitiator else { return } // responder waits for the inbound offer
        let handle = rtcCreateDataChannel(pc, Self.dataChannelLabel)
        attach(dataChannel: handle)
    }

    func acceptOffer(sdp: String) {
        guard rtcSetRemoteDescription(pc, sdp, "offer") >= 0 else { return }
        // With auto-negotiation, setting the remote offer makes libdatachannel emit the answer via
        // the local-description callback. Call setLocalDescription("answer") to be explicit/portable.
        _ = rtcSetLocalDescription(pc, "answer")
    }

    func acceptAnswer(sdp: String) {
        _ = rtcSetRemoteDescription(pc, sdp, "answer")
    }

    func addRemoteCandidate(_ signal: WebRtcSignal) {
        guard let cand = signal.candidate, !cand.isEmpty else { return }
        _ = rtcAddRemoteCandidate(pc, cand, signal.sdpMid)
    }

    /// Waits up to `timeout` for the data channel to open. Returns `false` on timeout/failure.
    func waitOpen(timeout: TimeInterval) async -> Bool {
        if isOpen { return true }
        if isClosed { return false }
        return await openSignal.wait(timeout: timeout) ?? false
    }

    /// Opens (if needed) and sends `data` as a single binary message.
    func send(_ data: Data, openTimeout: TimeInterval) async -> Bool {
        guard await waitOpen(timeout: openTimeout) else { return false }
        lock.lock()
        let channel = dc
        lock.unlock()
        guard channel >= 0 else { return false }
        return data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Bool in
            let base = raw.bindMemory(to: CChar.self).baseAddress
            // size >= 0 ⇒ binary message of exactly that many bytes.
            return rtcSendMessage(channel, base, Int32(data.count)) >= 0
        }
    }

    func registerOnClosed(_ handler: @escaping @Sendable () -> Void) {
        lock.lock(); defer { lock.unlock() }
        onClosed = handler
    }

    func close() {
        markClosed()
        lock.lock()
        let channel = dc
        let peer = pc
        dc = -1
        pc = -1
        lock.unlock()
        if channel >= 0 {
            rtcSetUserPointer(channel, nil)
            rtcClose(channel)
            rtcDelete(channel)
        }
        if peer >= 0 {
            rtcSetUserPointer(peer, nil)
            rtcClosePeerConnection(peer)
            rtcDeletePeerConnection(peer)
        }
    }

    // MARK: - Data channel wiring

    private func attach(dataChannel handle: Int32) {
        guard handle >= 0 else { return }
        lock.lock()
        dc = handle
        lock.unlock()
        rtcSetUserPointer(handle, Unmanaged.passUnretained(self).toOpaque())
        rtcSetOpenCallback(handle, Self.onChannelOpen)
        rtcSetClosedCallback(handle, Self.onChannelClosed)
        rtcSetErrorCallback(handle, Self.onChannelError)
        rtcSetMessageCallback(handle, Self.onChannelMessage)
        // Channel may already be open before the open callback is wired — surface it immediately.
        if rtcIsOpen(handle) {
            openSignal.resolve(true)
        }
    }

    // MARK: - Instance callbacks (invoked from the C trampolines)

    private func handleLocalDescription(sdp: String, type: String) {
        let signalType: WebRtcSignalType = (type == "offer") ? .offer : .answer
        let signal = WebRtcSignal(
            fromUhid: localUhid, toUhid: peerUhid, type: signalType, sdp: sdp)
        let signaling = self.signaling
        Task { await signaling.send(peerUhid: self.peerUhid, signal: signal) }
    }

    private func handleLocalCandidate(candidate: String, mid: String?) {
        let signal = WebRtcSignal(
            fromUhid: localUhid, toUhid: peerUhid, type: .iceCandidate,
            candidate: candidate, sdpMid: mid)
        let signaling = self.signaling
        Task { await signaling.send(peerUhid: self.peerUhid, signal: signal) }
    }

    private func handleStateChange(_ state: rtcState) {
        switch state {
        case RTC_FAILED, RTC_DISCONNECTED, RTC_CLOSED:
            markClosed()
        default:
            break
        }
    }

    private func handleDataChannel(_ handle: Int32) {
        attach(dataChannel: handle) // responder side receives the channel
    }

    private func handleChannelOpen() {
        openSignal.resolve(true)
    }

    private func handleChannelMessage(ptr: UnsafePointer<CChar>?, size: Int32) {
        guard let ptr else { return }
        let data: Data
        if size < 0 {
            // size < 0 ⇒ null-terminated string message; -size is unused, copy up to the NUL.
            data = Data(bytes: ptr, count: strlen(ptr))
        } else {
            data = Data(bytes: ptr, count: Int(size))
        }
        onData(peerUhid, data)
    }

    private func markClosed() {
        lock.lock()
        if closed {
            lock.unlock()
            return
        }
        closed = true
        let handler = onClosed
        lock.unlock()
        openSignal.resolve(false)
        handler?()
    }

    // MARK: - C trampolines (static, recover `self` from the user pointer)

    private static func link(from ptr: UnsafeMutableRawPointer?) -> WebRtcPeerLink? {
        guard let ptr else { return nil }
        return Unmanaged<WebRtcPeerLink>.fromOpaque(ptr).takeUnretainedValue()
    }

    private static let onLocalDescription: rtcDescriptionCallbackFunc = { _, sdp, type, ptr in
        guard let link = link(from: ptr), let sdp, let type else { return }
        link.handleLocalDescription(sdp: String(cString: sdp), type: String(cString: type))
    }

    private static let onLocalCandidate: rtcCandidateCallbackFunc = { _, cand, mid, ptr in
        guard let link = link(from: ptr), let cand else { return }
        link.handleLocalCandidate(
            candidate: String(cString: cand),
            mid: mid.map { String(cString: $0) })
    }

    private static let onStateChange: rtcStateChangeCallbackFunc = { _, state, ptr in
        link(from: ptr)?.handleStateChange(state)
    }

    private static let onDataChannel: rtcDataChannelCallbackFunc = { _, dc, ptr in
        link(from: ptr)?.handleDataChannel(dc)
    }

    private static let onChannelOpen: rtcOpenCallbackFunc = { _, ptr in
        link(from: ptr)?.handleChannelOpen()
    }

    private static let onChannelClosed: rtcClosedCallbackFunc = { _, ptr in
        link(from: ptr)?.markClosed()
    }

    private static let onChannelError: rtcErrorCallbackFunc = { _, _, ptr in
        link(from: ptr)?.markClosed()
    }

    private static let onChannelMessage: rtcMessageCallbackFunc = { _, message, size, ptr in
        link(from: ptr)?.handleChannelMessage(ptr: message, size: size)
    }

    // MARK: - PeerConnection construction

    /// Builds an `RTCPeerConnection`. `iceServers` is held alive (as C strings) only for the
    /// duration of the `rtcCreatePeerConnection` call, which copies what it needs.
    private static func createPeerConnection(iceServers: [String]) -> Int32 {
        var config = rtcConfiguration()
        // Auto-negotiation on: creating the data channel / setting the remote offer drives the SDP
        // exchange and fires the local-description callback (no manual setLocalDescription needed to
        // produce the offer).
        config.disableAutoNegotiation = false

        // Empty list ⇒ host-candidate-only ICE: no servers, no force-unwrap of a nil base address.
        if iceServers.isEmpty {
            config.iceServers = nil
            config.iceServersCount = 0
            return rtcCreatePeerConnection(&config)
        }

        // Marshal [String] -> a C `const char **` array of NUL-terminated strings, all kept valid
        // until rtcCreatePeerConnection returns (it copies what it needs). The config is built and
        // the create call issued inside the rebinding closure, so the rebound `const char **`
        // pointer is only ever used while it is guaranteed valid.
        let cStrings: [UnsafeMutablePointer<CChar>?] = iceServers.map { strdup($0) }
        defer { cStrings.forEach { free($0) } }

        return cStrings.withUnsafeBufferPointer { buf -> Int32 in
            buf.baseAddress!.withMemoryRebound(
                to: UnsafePointer<CChar>?.self, capacity: buf.count
            ) { rebased -> Int32 in
                // rtc's `iceServers` field imports as a (non-const) UnsafeMutablePointer even
                // though the C API treats it as read-only; the rebound buffer is immutable, so
                // cast to mutable. The pointee is only read during rtcCreatePeerConnection, which
                // is called inside this closure while `rebased` is guaranteed valid.
                config.iceServers = UnsafeMutablePointer(mutating: rebased)
                config.iceServersCount = Int32(iceServers.count)
                return rtcCreatePeerConnection(&config)
            }
        }
    }
}

// MARK: - OneShot

/// A one-shot async signal: the first `resolve` wins; later resolves are ignored. `wait` returns the
/// resolved value, or `nil` on timeout. Backed by continuations so waiters never busy-spin.
private final class OneShot<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value?
    private var waiters: [CheckedContinuation<Value, Never>] = []

    func resolve(_ newValue: Value) {
        lock.lock()
        guard value == nil else { lock.unlock(); return }
        value = newValue
        let pending = waiters
        waiters.removeAll()
        lock.unlock()
        for waiter in pending { waiter.resume(returning: newValue) }
    }

    /// Waits for resolution up to `timeout` seconds. Returns the value, or `nil` on timeout.
    func wait(timeout: TimeInterval) async -> Value? {
        await withTaskGroup(of: Value?.self) { group in
            group.addTask { await self.waitForever() }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
    }

    private func waitForever() async -> Value {
        await withCheckedContinuation { (continuation: CheckedContinuation<Value, Never>) in
            lock.lock()
            if let value {
                lock.unlock()
                continuation.resume(returning: value)
            } else {
                waiters.append(continuation)
                lock.unlock()
            }
        }
    }
}
