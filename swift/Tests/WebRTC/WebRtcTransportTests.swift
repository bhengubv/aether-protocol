// SPDX-License-Identifier: MIT
// Loopback tests for the real WebRTC P2P transport (libdatachannel-backed).

import XCTest
import Foundation
@testable import AetherNetWebRTC
@testable import AetherNetWebRTCSignaling
@testable import AetherNetProtocol

final class WebRtcTransportTests: XCTestCase {

    /// Stands up two real `WebRtcTransportService` instances wired only through an in-process
    /// signalling bus — no central server, no STUN — and proves a direct data channel negotiates
    /// over host candidates and carries bytes.
    func testTwoPeersExchangeBytesNoServer() async throws {
        let bus = InMemoryWebRtcSignalingBus()
        defer { Task { await bus.close() } }

        // Empty (not nil) ICE list ⇒ host-candidate-only ICE, no network dependency.
        let hostOnly: [String] = []

        let alice = WebRtcTransportService(
            localUhid: "alice", signaling: await bus.endpoint("alice"), iceServers: hostOnly)
        let bob = WebRtcTransportService(
            localUhid: "bob", signaling: await bus.endpoint("bob"), iceServers: hostOnly)
        defer { alice.close(); bob.close() }

        let received = ReceivedBox()
        bob.onDataReceived { from, data in
            if from == "alice" { received.set(data) }
        }

        let payload = Data("hello over a serverless webrtc datachannel".utf8)
        let ok = await alice.sendAsync(peerUhid: "bob", data: payload, cancellationToken: nil)
        XCTAssertTrue(ok, "send should succeed once the data channel opens")

        // Poll for arrival with a 30s ceiling (negotiation runs on libdatachannel's own threads).
        let got = try await received.wait(timeout: 30)
        XCTAssertEqual(got, payload, "payload must cross the data channel unchanged")

        XCTAssertTrue(alice.isConnected(peerUhid: "bob"), "alice should report connected to bob")
        XCTAssertTrue(bob.isConnected(peerUhid: "alice"), "bob should report connected to alice")
    }

    /// Checks the ladder-facing metadata.
    func testTransportMetadata() async {
        let bus = InMemoryWebRtcSignalingBus()
        defer { Task { await bus.close() } }

        let t = WebRtcTransportService(
            localUhid: "x", signaling: await bus.endpoint("x"), iceServers: [])
        defer { t.close() }

        XCTAssertEqual("WebRTC P2P", t.name)
        XCTAssertTrue(t.isAvailable)
        XCTAssertEqual(0, t.maxRangeMeters, "internet range is 0 (unbounded)")
        XCTAssertEqual(256, t.maxConcurrentPeers)
        XCTAssertNotNil(t.metrics)
    }

    /// The in-process signalling bus must route a signal to its addressee, in order, off the
    /// sender's stack.
    func testInMemoryBusRoutesByUhid() async throws {
        let bus = InMemoryWebRtcSignalingBus()
        defer { Task { await bus.close() } }

        let aliceEp = await bus.endpoint("alice")
        let bobEp = await bus.endpoint("bob")

        let inbox = ReceivedBox()
        await bobEp.onSignal { signal in
            inbox.set(Data(signal.sdp.map { Array($0.utf8) } ?? []))
        }

        let signal = WebRtcSignal(
            fromUhid: "alice", toUhid: "bob", type: .offer, sdp: "v=0 fake-offer")
        let delivered = await aliceEp.send(peerUhid: "bob", signal: signal)
        XCTAssertTrue(delivered, "signal addressed to a known endpoint should route")

        let got = try await inbox.wait(timeout: 5)
        XCTAssertEqual(String(decoding: got, as: UTF8.self), "v=0 fake-offer")
    }
}

// MARK: - Test helpers

/// A thread-safe one-shot box that an async test can await without busy-spinning the actor system.
private final class ReceivedBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Data?

    func set(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        if value == nil { value = data }
    }

    private func get() -> Data? {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    /// Polls until a value lands or `timeout` seconds elapse; throws on timeout.
    func wait(timeout: TimeInterval) async throws -> Data {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let value = get() { return value }
            try await Task.sleep(nanoseconds: 20_000_000) // 20 ms
        }
        throw XCTSkip("timed out waiting for bytes over the data channel")
    }
}
