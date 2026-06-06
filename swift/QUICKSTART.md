<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Swift Quickstart

A 5-minute wiring guide for adopting the Swift implementation.
Covers identity setup, pre-key bundle exchange, encrypt / decrypt,
persistence-at-rest pointer, and cross-language fixture interop.

## 1. Add the package

The implementation lives in this repository's `swift/` subtree as a
Swift Package. Add it to your `Package.swift`:

```swift
.package(url: "https://github.com/bhengubv/aether-protocol.git", from: "1.0.0")
```

then declare the target dependency:

```swift
.product(name: "AetherMeshProtocol", package: "aether-protocol")
```

The single runtime dependency is Apple's
[swift-crypto](https://github.com/apple/swift-crypto) — Curve25519,
HKDF-SHA256, and AES-GCM all come from there. No third-party crypto
library is pulled in. Targets: macOS 13+ / iOS 16+.

## 2. Identity setup

Each node has two long-term identity keypairs — Ed25519 for signing
pre-key bundles, X25519 for X3DH ECDH. `SignalProtocolService` is an
`actor` that generates them on first construction:

```swift
import AetherMeshProtocol

let alice = SignalProtocolService()
await alice.setLocalUhid("alice-uhid-0001")
```

`setLocalUhid` is required before any `encrypt(...)` call — the local
UHID is stamped into every outbound `EncryptedPayload`.

## 3. Generate and publish a pre-key bundle

A pre-key bundle is what other nodes need to initiate a Signal session
with you. It contains both identity public keys (Ed25519 + X25519),
your active signed pre-key with Ed25519 signature, and one OPK from
the pool:

```swift
let bundle = try await alice.generatePreKeyBundle(localUhid: "alice-uhid-0001")
// Publish bundle.identityKey, bundle.identityKeyX25519,
// bundle.signedPreKey, bundle.signedPreKeySignature, bundle.preKey,
// bundle.preKeyId, bundle.signedPreKeyId wherever your peers fetch them.
```

The OPK pool is topped up to `opkPoolSize` (default 100) on every
bundle generation. Each OPK is single-use — the responder consumes it
during X3DH and never hands the same id out twice.

## 4. Process a peer's bundle (initiator side)

When you fetch Bob's bundle, hand it to `processPreKeyBundle`. This
runs X3DH (4× X25519 + HKDF) and primes the Double-Ratchet state for
your first encrypt to Bob:

```swift
let bobBundle: PreKeyBundle = try await fetchBobsBundle()
try await alice.processPreKeyBundle(bobBundle)
```

The signed pre-key signature is verified against the responder's
Ed25519 identity key inside `processPreKeyBundle` — a tampered bundle
throws `SignalProtocolError.signatureVerificationFailed`.

## 5. Encrypt and decrypt

The first message Alice sends to Bob carries her X3DH inputs as a
PreKey message (`messageType == 1`); Bob runs his side of X3DH on
receive. Subsequent messages are normal Double-Ratchet messages with
DH-ratchet steps re-keying the chain on every roundtrip:

```swift
let plaintext = "hello, mesh".data(using: .utf8)!
let payload = try await alice.encrypt(peerUhid: "bob-uhid-0001", plaintext: plaintext)

// Wire-format the payload via your transport — fields are Data,
// except senderUhid (String), counter (Int32), and messageType (Int32).

let recovered = try await bob.decrypt(peerUhid: "alice-uhid-0001", payload: payload)
print(String(data: recovered, encoding: .utf8) ?? "")  // "hello, mesh"
```

## 6. Wire packets through `PacketSerializer`

The Signal-protocol `EncryptedPayload` is the inner envelope. Wrap it
in a `MeshPacket` for transport across the mesh:

```swift
let inner = try JSONEncoder().encode(payload)
var pkt = MeshPacket(
    type: .data,
    sourceUhid: "alice-uhid-0001",
    destinationUhid: "bob-uhid-0001",
    payload: inner
)
pkt.packetNonce = randomBytes(8)
let wire: Data = PacketSerializer.serialize(pkt)
// Hand `wire` to your transport (BLE, Wi-Fi-Direct, NearLink, etc.)

let received = try PacketSerializer.deserialize(wire)
let payload2 = try JSONDecoder().decode(EncryptedPayload.self, from: received.payload)
```

`PacketSerializer.deserialize` throws `PacketSerializationError` on
any wire-format failure — that's the only exception type a host
needs to catch on the receive path.

## 7. Persistence

Sessions, identity keys, signed-pre-key history, and the OPK pool
are held in-actor. Persistence pointers in the C# / Go / Python ports
(`KeyValueSignalSessionStore`, `KeyValuePreKeyStore`) have not yet
landed in the Swift port — for now, treat the actor as
process-local. To survive restarts, snapshot the `PreKeyState`
returned from `generatePreKeyBundle` and replay the bundle exchange
on restart.

For encryption-at-rest of any persisted blobs (when the Swift
KV-store layer lands), follow the C# / Go / TS pattern — wrap the
inner KV with an AES-256-GCM envelope keyed off a master key from
your Keychain.

## 8. Cross-language interop

Wire compatibility across the eight implementation families is
anchored by `fixtures/` at the repository root. The Swift verifier is
`Tests/SignalFixtureTests.swift` — it loads the same `fixtures/signal/expected/*.json`
the C# / Go / Python / Rust / TypeScript / Kotlin / C verifiers
load, and asserts byte-identical X3DH and ratchet outputs.

If `swift test --filter SignalFixture` passes, your Swift host can
talk to a C# / Go / Python / Rust / Kotlin / TypeScript / C host on
the wire.

## 9. Running tests, property tests, and benches

```bash
# Full suite — unit + property + fixture verifiers.
cd swift
swift test

# Property / fuzz-style tests only — 1000 iterations per property,
# seeded LCG for reproducibility.
swift test --filter PropertyTests

# Benchmark harness — XCTest measure blocks, 11 cases mirroring the
# C# / Go / Python / TS / C harness.
swift test --filter Benchmark

# Cross-language fixture verifier only.
swift test --filter Fixture
```

See `BENCHMARKS.md` for what each bench case measures and the
regression gate used in CI.
