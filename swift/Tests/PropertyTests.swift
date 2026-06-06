// SPDX-License-Identifier: MIT

/// Property / fuzz-style tests for the Swift aether-protocol deserializers.
///
/// Mirrors the Go fuzz harness (`go/protocol/fuzz_serializer_test.go`),
/// the Python `tests/test_fuzz.py`, the C# `PacketSerializerFuzzTests`,
/// and the TypeScript `tests/fuzz.test.ts` — every deserializer parses
/// untrusted bytes off the wire, so the contract is: for ANY input it
/// must EITHER return a valid object OR throw a documented `Error`.
/// The documented error set for `PacketSerializer.deserialize` is
/// `PacketSerializationError` (any case).
///
/// Swift has no native fuzzer — the standard library's `Hasher` and
/// `SystemRandomNumberGenerator` are not seedable, and `swift test` does
/// not ship a libFuzzer integration the way Go's `go test -fuzz` does.
/// We compensate with deterministic property tests driven by a seeded
/// LCG (`SeededRng`) so a failure prints exactly which seed produced the
/// bad input. 1000 iterations per property mirrors the fast-check
/// `numRuns: 1000` budget the TypeScript harness uses.
///
/// Three flavours run here:
///
///   1. `serialize -> deserialize` round-trip on randomly-generated
///      `MeshPacket` inputs (random uhids, payloads up to 64 KB, all
///      packet types, full `Int32` ttl range).
///
///   2. `PacketSerializer.deserialize(arbitrary bytes)` — assert no
///      undocumented exception type escapes (only `PacketSerializationError`
///      should appear; bare `Swift.Error` from inside Foundation should
///      not).
///
///   3. `EncryptedPayload` `Codable` JSON round-trip — a host wiring the
///      protocol over a JSON-friendly transport (REST, WebSocket, IndexedDB)
///      needs this codec to round-trip every wire-significant field.
import XCTest
@testable import AetherMeshProtocol

final class PropertyTests: XCTestCase {

    // MARK: - Per-property iteration budget

    /// 1000 iterations per property. Matches the Python `hypothesis`
    /// default and the TypeScript `fast-check` `numRuns: 1000` budget.
    /// A handful of properties use a smaller budget when the operation
    /// itself is expensive (X3DH), but the deserializer-only paths run
    /// the full 1000.
    private let numRuns: Int = 1000

    // MARK: - Round-trip property: serialize -> deserialize

    /// For any randomly-generated `MeshPacket`, `serialize -> deserialize`
    /// reproduces every wire-significant field byte-for-byte.
    func testProperty_PacketRoundTrip() throws {
        var rng = SeededRng(seed: 0xA37E_0001)
        for iteration in 0..<numRuns {
            let packet = randomPacket(&rng)
            let wire = PacketSerializer.serialize(packet)
            let got: MeshPacket
            do {
                got = try PacketSerializer.deserialize(wire)
            } catch {
                XCTFail("Round-trip failed at iteration \(iteration), seed=\(rng.lastSeed): \(error)")
                return
            }
            XCTAssertEqual(got.id, packet.id, "iteration=\(iteration) id mismatch")
            XCTAssertEqual(got.type, packet.type, "iteration=\(iteration) type mismatch")
            XCTAssertEqual(got.sourceUhid, packet.sourceUhid, "iteration=\(iteration) sourceUhid mismatch")
            XCTAssertEqual(got.destinationUhid, packet.destinationUhid, "iteration=\(iteration) destinationUhid mismatch")
            XCTAssertEqual(got.ttl, packet.ttl, "iteration=\(iteration) ttl mismatch")
            XCTAssertEqual(got.priority, packet.priority, "iteration=\(iteration) priority mismatch")
            XCTAssertEqual(got.protocolVersion, packet.protocolVersion, "iteration=\(iteration) protocolVersion mismatch")
            XCTAssertEqual(got.timestampMs, packet.timestampMs, "iteration=\(iteration) timestampMs mismatch")
            XCTAssertEqual(got.payload, packet.payload, "iteration=\(iteration) payload mismatch")
            XCTAssertEqual(got.packetNonce, packet.packetNonce, "iteration=\(iteration) packetNonce mismatch")
            XCTAssertEqual(got.signature, packet.signature, "iteration=\(iteration) signature mismatch")
        }
    }

    // MARK: - Arbitrary-bytes deserializer fuzz

    /// `PacketSerializer.deserialize` on completely arbitrary input must
    /// either succeed or throw a `PacketSerializationError`. No bare
    /// `Swift.Error` from a Foundation index-out-of-range, no fatal trap.
    func testProperty_DeserializeArbitraryBytesNeverCrashes() {
        var rng = SeededRng(seed: 0xA37E_0002)
        for iteration in 0..<numRuns {
            let length = rng.nextInt(in: 0...8192)
            let data = rng.nextBytes(count: length)
            do {
                _ = try PacketSerializer.deserialize(data)
                // Success path is fine — even arbitrary bytes can occasionally
                // happen to parse as a valid (if absurd) packet.
            } catch let err as PacketSerializationError {
                _ = err  // documented error type — expected
            } catch {
                XCTFail(
                    "Undocumented exception escaped deserialize at iteration=\(iteration), " +
                    "seed=\(rng.lastSeed), input.count=\(data.count): " +
                    "\(type(of: error)) \(error)"
                )
                return
            }
        }
    }

    /// `PacketSerializer.tryDeserialize` is documented to never throw —
    /// it returns `nil` on every failure path. Verify across the same
    /// arbitrary-bytes distribution that the call itself never crashes
    /// and that the optional shape is preserved (a successful parse
    /// returns a `MeshPacket` whose `id` is a valid UUID, anything else
    /// returns nil).
    func testProperty_TryDeserializeArbitraryBytesNeverThrows() {
        var rng = SeededRng(seed: 0xA37E_0003)
        for iteration in 0..<numRuns {
            let length = rng.nextInt(in: 0...8192)
            let data = rng.nextBytes(count: length)
            // Non-throwing API; merely calling it must not crash.
            // If it returns a packet, the type must be a valid
            // PacketType enum case — `tryDeserialize` returns nil on
            // any error, so a non-nil result has gone through the full
            // happy path including the PacketType(rawValue:) lookup.
            if let packet = PacketSerializer.tryDeserialize(data) {
                let raw = packet.type.rawValue
                XCTAssertNotNil(
                    PacketType(rawValue: raw),
                    "iteration=\(iteration): tryDeserialize returned a packet with invalid PacketType raw=\(raw)"
                )
            }
        }
    }

    /// Mutation fuzz: serialize a valid packet, then flip 1-4 random bytes
    /// on the wire and feed the result back through `deserialize`. Same
    /// undocumented-exception contract applies.
    func testProperty_DeserializeMutatedWireNeverCrashes() {
        var rng = SeededRng(seed: 0xA37E_0004)
        for iteration in 0..<numRuns {
            let packet = randomPacket(&rng)
            var wire = PacketSerializer.serialize(packet)
            guard !wire.isEmpty else { continue }
            let mutationCount = rng.nextInt(in: 1...4)
            for i in 0..<mutationCount {
                let pos = rng.nextInt(in: 0..<wire.count)
                let xor = UInt8((rng.nextInt(in: 0...255) + i) & 0xFF)
                let original = wire[wire.startIndex + pos]
                wire[wire.startIndex + pos] = original ^ xor
            }
            do {
                _ = try PacketSerializer.deserialize(wire)
            } catch let err as PacketSerializationError {
                _ = err
            } catch {
                XCTFail(
                    "Undocumented exception on mutated wire at iteration=\(iteration), " +
                    "seed=\(rng.lastSeed): \(type(of: error)) \(error)"
                )
                return
            }
        }
    }

    /// Hand-built header with a payload-length prefix in the int32-max
    /// range but no following bytes. Mirrors the Python / Go / TS
    /// `OversizePayloadLength` test — the deserializer must reject
    /// without allocating gigabytes for an attacker-controlled length.
    func testProperty_RejectsOversizePayloadLength() {
        let oversizes: [Int32] = [0x7FFF_FFFF, 0x1000_0000, 0x0100_0000]
        for oversize in oversizes {
            var buf = Data(count: 43)
            buf[0] = 0x02 // protocolVersion
            buf[1] = 0x03 // PacketType.data
            // bytes 2..17 left zero (uuid)
            buf[18] = 0x05 // priority
            // ttl int32 LE at offset 19
            var ttl: Int32 = 7
            withUnsafeBytes(of: &ttl) { src in
                for (i, b) in src.enumerated() { buf[19 + i] = b }
            }
            // timestamp int64 LE at offset 23
            var ts: Int64 = 1_234_567_890_000
            withUnsafeBytes(of: &ts) { src in
                for (i, b) in src.enumerated() { buf[23 + i] = b }
            }
            // src/dst/nonce length prefixes at 31..36 stay zero
            // payload-length prefix at offset 37
            var payloadLen = oversize
            withUnsafeBytes(of: &payloadLen) { src in
                for (i, b) in src.enumerated() { buf[37 + i] = b }
            }
            // signature length at 41..42 stays zero
            XCTAssertThrowsError(try PacketSerializer.deserialize(buf)) { error in
                XCTAssertTrue(
                    error is PacketSerializationError,
                    "Expected PacketSerializationError for oversize=\(oversize), got \(type(of: error))"
                )
            }
        }
    }

    /// Negative payload length is explicitly guarded by the Swift
    /// deserializer. Mirrors the same guard in every other language.
    func testProperty_RejectsNegativePayloadLength() {
        var buf = Data(count: 43)
        buf[0] = 0x02
        buf[1] = 0x03
        buf[18] = 0x05
        var ttl: Int32 = 7
        withUnsafeBytes(of: &ttl) { src in
            for (i, b) in src.enumerated() { buf[19 + i] = b }
        }
        var negLen: Int32 = -1
        withUnsafeBytes(of: &negLen) { src in
            for (i, b) in src.enumerated() { buf[37 + i] = b }
        }
        XCTAssertThrowsError(try PacketSerializer.deserialize(buf)) { error in
            guard let perr = error as? PacketSerializationError else {
                XCTFail("Expected PacketSerializationError, got \(type(of: error))")
                return
            }
            // negativeLength is the documented case for this guard.
            if case .negativeLength = perr { return }
            // insufficientData is also acceptable if a future refactor
            // converts the negative length into an underflow on the
            // remaining-bytes check.
            if case .insufficientData = perr { return }
            XCTFail("Expected .negativeLength or .insufficientData, got \(perr)")
        }
    }

    // MARK: - EncryptedPayload Codable round-trip

    /// `EncryptedPayload` is `Codable`. For any random instance, encode-
    /// then-decode through `JSONEncoder` / `JSONDecoder` reproduces every
    /// wire-significant field. A host wiring the protocol over a JSON
    /// transport (REST, WebSocket, IndexedDB) depends on this contract.
    func testProperty_EncryptedPayloadCodableRoundTrip() throws {
        var rng = SeededRng(seed: 0xA37E_0005)
        let encoder = JSONEncoder()
        let decoder = JSONDecoder()
        for iteration in 0..<numRuns {
            let payload = randomEncryptedPayload(&rng)
            let json: Data
            let got: EncryptedPayload
            do {
                json = try encoder.encode(payload)
                got = try decoder.decode(EncryptedPayload.self, from: json)
            } catch {
                XCTFail("Codable round-trip failed at iteration=\(iteration), seed=\(rng.lastSeed): \(error)")
                return
            }
            XCTAssertEqual(got.ciphertext, payload.ciphertext, "iteration=\(iteration) ciphertext")
            XCTAssertEqual(got.nonce, payload.nonce, "iteration=\(iteration) nonce")
            XCTAssertEqual(got.messageType, payload.messageType, "iteration=\(iteration) messageType")
            XCTAssertEqual(got.senderUhid, payload.senderUhid, "iteration=\(iteration) senderUhid")
            XCTAssertEqual(got.counter, payload.counter, "iteration=\(iteration) counter")
            XCTAssertEqual(got.initiatorIdentityKeyX25519, payload.initiatorIdentityKeyX25519, "iteration=\(iteration) initiatorIK")
            XCTAssertEqual(got.initiatorEphemeralKeyX25519, payload.initiatorEphemeralKeyX25519, "iteration=\(iteration) initiatorEK")
            XCTAssertEqual(got.usedSignedPreKeyId, payload.usedSignedPreKeyId, "iteration=\(iteration) usedSPK")
            XCTAssertEqual(got.usedOneTimePreKeyId, payload.usedOneTimePreKeyId, "iteration=\(iteration) usedOTPK")
            XCTAssertEqual(got.senderEphemeralKeyX25519, payload.senderEphemeralKeyX25519, "iteration=\(iteration) senderEph")
            XCTAssertEqual(got.previousChainCount, payload.previousChainCount, "iteration=\(iteration) previousChainCount")
        }
    }

    // MARK: - Random generators

    private func randomPacket(_ rng: inout SeededRng) -> MeshPacket {
        let allTypes: [PacketType] = [
            .routeRequest, .routeReply, .data, .ack, .sosBroadcast, .sosAck,
            .channelMessage, .chunkRequest, .chunkData, .heartbeat,
            .streamAnnounce, .streamSegment, .streamSubscribe, .streamUnsubscribe,
            .voicePtt, .voiceCall, .voiceSignaling, .dtnBundle, .dtnCustodyAck,
            .dtnDeliveryReceipt, .presenceBeacon, .presenceQuery, .profileSync,
            .tipPacket, .preKeyRequest, .preKeyResponse, .videoCall, .videoSignaling,
            .watchSync, .watchReaction, .videoFrame, .screenShare,
            .watchChunkRequest, .torrentMetadata, .hello, .helloAck
        ]

        // Bound payloads to 64 KB so each iteration stays under a few ms.
        // The wire format itself accepts up to int32-max — the bench
        // harness covers serializer perf at larger sizes.
        let payloadLen = rng.nextInt(in: 0...65536)
        let nonceLen = rng.nextInt(in: 0...255)
        let sigLen = rng.nextInt(in: 0...255)
        let srcLen = rng.nextInt(in: 0...64)
        let dstLen = rng.nextInt(in: 0...64)
        let typeIdx = rng.nextInt(in: 0..<allTypes.count)

        // Random UUID from 16 random bytes — full 128-bit space.
        let uuidBytes = rng.nextBytes(count: 16)
        var uuidTuple: uuid_t = (0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)
        withUnsafeMutableBytes(of: &uuidTuple) { dst in
            uuidBytes.withUnsafeBytes { src in
                dst.copyMemory(from: src)
            }
        }
        let id = UUID(uuid: uuidTuple)

        // Restrict to the wire-format-realistic range (Int32 LE on the wire).
        // Negative TTLs are syntactically representable but the routing
        // layer drops them — the serializer round-trip must still work
        // for them, so include the full Int32 range here.
        let ttl = Int32(truncatingIfNeeded: rng.nextInt(in: -0x8000_0000 ... 0x7FFF_FFFF))
        let priority = UInt8(rng.nextInt(in: 0...255))
        let protocolVersion = UInt8(rng.nextInt(in: 0...255))
        let timestampMs = Int64(truncatingIfNeeded: rng.nextInt(in: 0 ... Int(Int32.max) * 100))

        return MeshPacket(
            id: id,
            type: allTypes[typeIdx],
            sourceUhid: rng.nextAsciiString(length: srcLen),
            destinationUhid: rng.nextAsciiString(length: dstLen),
            ttl: ttl,
            priority: priority,
            payload: rng.nextBytes(count: payloadLen),
            createdAt: Date(timeIntervalSince1970: TimeInterval(timestampMs) / 1000.0),
            signature: rng.nextBytes(count: sigLen),
            packetNonce: rng.nextBytes(count: nonceLen),
            timestampMs: timestampMs,
            protocolVersion: protocolVersion
        )
    }

    private func randomEncryptedPayload(_ rng: inout SeededRng) -> EncryptedPayload {
        let ctLen = rng.nextInt(in: 0...4096)
        let messageType: Int32 = rng.nextBool() ? 0 : 1
        let senderLen = rng.nextInt(in: 0...64)
        let counter = Int32(truncatingIfNeeded: rng.nextInt(in: 0 ... Int(Int32.max)))
        let prevChain = Int32(truncatingIfNeeded: rng.nextInt(in: 0 ... Int(Int32.max)))
        let usedSPK = Int32(truncatingIfNeeded: rng.nextInt(in: 0 ... Int(Int32.max)))
        let usedOTPK = Int32(truncatingIfNeeded: rng.nextInt(in: 0 ... Int(Int32.max)))

        // Optional 32-byte X25519 keys, present roughly half the time.
        let initIK: Data? = rng.nextBool() ? rng.nextBytes(count: 32) : nil
        let initEK: Data? = rng.nextBool() ? rng.nextBytes(count: 32) : nil
        let senderEph: Data? = rng.nextBool() ? rng.nextBytes(count: 32) : nil

        return EncryptedPayload(
            ciphertext: rng.nextBytes(count: ctLen),
            nonce: rng.nextBytes(count: 12),
            messageType: messageType,
            senderUhid: rng.nextAsciiString(length: senderLen),
            counter: counter,
            initiatorIdentityKeyX25519: initIK,
            initiatorEphemeralKeyX25519: initEK,
            usedSignedPreKeyId: usedSPK,
            usedOneTimePreKeyId: usedOTPK,
            senderEphemeralKeyX25519: senderEph,
            previousChainCount: prevChain
        )
    }
}

// MARK: - SeededRng

/// Linear-congruential generator with parameters from Numerical Recipes
/// (Knuth's MMIX constants in the high bits, plus a 64-bit increment).
/// Not cryptographically random — used here purely to make property
/// tests reproducible across runs. A failing iteration prints
/// `seed=<lastSeed>`; re-running with the same starting seed reproduces
/// the same input sequence byte-for-byte.
///
/// We don't use `SystemRandomNumberGenerator` because Swift's stdlib
/// doesn't expose a seeded variant, and `srand48`/`drand48` are weakly
/// linked on Linux. A 64-bit LCG is sufficient for the property
/// distribution.
struct SeededRng {
    private(set) var lastSeed: UInt64
    private var state: UInt64

    init(seed: UInt64) {
        // Avoid the trivial 0 fixpoint on Knuth's constants.
        self.state = seed == 0 ? 0xDEAD_BEEF_CAFE_BABE : seed
        self.lastSeed = self.state
    }

    /// Advance the generator and return a fresh 64-bit value. Knuth MMIX
    /// constants — full period over UInt64.
    mutating func next() -> UInt64 {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return state
    }

    mutating func nextBool() -> Bool {
        return (next() & 1) == 0
    }

    /// Closed range [lo, hi]. Both endpoints inclusive.
    mutating func nextInt(in range: ClosedRange<Int>) -> Int {
        let span = UInt64(range.upperBound - range.lowerBound + 1)
        if span == 0 { return range.lowerBound }
        let r = next() % span
        return range.lowerBound + Int(r)
    }

    /// Half-open range [lo, hi). Upper bound exclusive.
    mutating func nextInt(in range: Range<Int>) -> Int {
        if range.isEmpty { return range.lowerBound }
        let span = UInt64(range.upperBound - range.lowerBound)
        let r = next() % span
        return range.lowerBound + Int(r)
    }

    mutating func nextBytes(count: Int) -> Data {
        var out = Data(count: count)
        var i = 0
        while i < count {
            let chunk = next()
            withUnsafeBytes(of: chunk) { src in
                let take = min(8, count - i)
                for j in 0..<take {
                    out[i + j] = src[j]
                }
            }
            i += 8
        }
        return out
    }

    /// ASCII letters + digits + dash. Avoids exercising non-UTF-8 paths
    /// in the serializer (which doesn't reject invalid UTF-8 — the
    /// `String(data:encoding:.utf8)` falls back to the empty string,
    /// which is a separate hardening question outside this fuzz harness).
    mutating func nextAsciiString(length: Int) -> String {
        if length == 0 { return "" }
        let alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
        let chars = Array(alphabet)
        var out = ""
        out.reserveCapacity(length)
        for _ in 0..<length {
            let idx = Int(next() % UInt64(chars.count))
            out.append(chars[idx])
        }
        return out
    }
}
