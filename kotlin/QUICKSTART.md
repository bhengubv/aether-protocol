<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Kotlin Quickstart

A 5-minute wiring guide for adopting the Kotlin/JVM implementation.
Covers identity setup, pre-key bundle exchange, encrypt / decrypt,
persistence pointers, and cross-language interop.

## 1. Install

The implementation lives in this repository's `kotlin/` subtree. For a
local dev build from a checkout:

```bash
cd kotlin
./gradlew build
```

Runtime dependencies (declared in `build.gradle.kts`):

- `org.bouncycastle:bcprov-jdk18on` — Ed25519, X25519, AES-GCM, HKDF
- `org.jetbrains.kotlinx:kotlinx-coroutines-core` — async transport surface
- `org.slf4j:slf4j-api` + `slf4j-simple` — logging

JDK 17+ is required (Kotlin 1.9 / `jvmToolchain(17)`).

## 2. Identity setup

Each node has two long-term identity keypairs — Ed25519 for signing
pre-key bundles, X25519 for X3DH ECDH. The service generates them on
first construction. You must call `setLocalUhid(uhid)` before encrypting
to anyone (or pass the UHID to `generatePreKeyBundle`, which sets it as
a side-effect).

```kotlin
import aether.security.SignalProtocol

val alice = SignalProtocol()
alice.setLocalUhid("alice-uhid-0001")
```

## 3. Generate and publish a pre-key bundle

A pre-key bundle is what other nodes need to initiate a Signal session
with you. It contains your two identity public keys, your active signed
pre-key (with Ed25519 signature), and one OPK from the pool.

```kotlin
val bundle = alice.generatePreKeyBundle("alice-uhid-0001")
// Publish bundle.identityKey, bundle.identityKeyX25519,
// bundle.signedPreKey, bundle.signedPreKeySignature, bundle.preKey,
// and bundle.preKeyId wherever your peers can fetch them.
```

The OPK pool is topped up to `opkPoolSize` (default 100, see the
`SignalProtocol(opkPoolSize: Int)` constructor) on every bundle
generation. Each OPK is single-use — the responder consumes it during
X3DH and never hands the same id out twice.

## 4. Process a peer's bundle (initiator side)

When you fetch Bob's bundle, hand it to `processPreKeyBundle`. This
runs X3DH (4× X25519 + HKDF) and stages the Double-Ratchet state for
your first encrypt to Bob.

```kotlin
val bobBundle: PreKeyBundle = fetchBobsBundle() // your transport / discovery
alice.processPreKeyBundle(bobBundle)
```

## 5. Encrypt and decrypt

The first message Alice sends to Bob carries her X3DH inputs as a
PreKey message (`messageType == SignalProtocol.MESSAGE_TYPE_PRE_KEY`,
i.e. `1`); Bob runs his side of X3DH on receive. Subsequent messages
are normal Double-Ratchet messages.

```kotlin
val payload = alice.encrypt("bob-uhid-0001", "hello, mesh".toByteArray())
// Wire-format the payload via your transport — fields are ByteArray,
// except senderUhid (String), counter (Int), and messageType (Int).

val plaintext = bob.decrypt("alice-uhid-0001", payload)
println(String(plaintext)) // "hello, mesh"
```

## 6. Persistent state — adopter responsibility

Unlike the Python and TypeScript implementations, the Kotlin module
does not yet ship a `KeyValueSignalSessionStore`-style abstraction.
`SignalSession` and `PreKeyStateInternal` are `internal` to the
`aether.security` package and hold the live ratchet state in memory
only. Adopters who need durable sessions can either:

1. Persist the published `PreKeyBundle` and re-establish on every
   restart — simple but loses the symmetric ratchet state (peers must
   re-X3DH after each restart).
2. Wire a custom store by extending the `SignalProtocol` class to
   expose snapshot/restore hooks — see the C# `IPreKeyStore` /
   `ISignalSessionStore` interfaces in
   `src/Aether.Protocol/Security/` for the API shape to mirror.

If you ship #2 to upstream, the property-test harness in
`src/test/kotlin/aether/property/PropertyTests.kt` already includes a
JSON codec round-trip property over `EncryptedPayload` — extend it to
cover your DTO and the wire-compat property tests carry over.

## 7. Encryption-at-rest

For at-rest encryption of session blobs you persist yourself, follow
the C# / TS / Python pattern: AES-256-GCM each value with a master key
held by your KMS. The `SignalProtocol` private `hkdf32` helper (and
the `kdfRk` 64-byte variant) are not exposed publicly, so derive your
own KDF — `javax.crypto.Mac.getInstance("HmacSHA256")` over a stored
salt is the lightest path.

The wire format is byte-identical to the C# / Go / Python / Rust /
Swift / Java / TypeScript references — a Kotlin host can decrypt blobs
written by any of them given the same key material and version registry.

## 8. Cross-language interop

Wire compatibility across the eight implementation families is anchored
by `fixtures/` at the repository root. The Kotlin verifier is
`src/test/kotlin/aether/protocol/FixtureTest.kt` — it loads
`fixtures/inputs.json` and asserts that the Kotlin `PacketSerializer`
produces byte-identical output to the canonical C# expected.
Signal-protocol fixtures under `fixtures/signal/` cover X3DH +
double-ratchet KDF outputs and are exercised by
`src/test/kotlin/aether/security/SignalFixtureTest.kt`.

If those verifiers pass (`./gradlew test`), your Kotlin host can talk
to a C# / Go / Python / Rust / TypeScript / Swift / Java host on the
wire.

## 9. Running tests and benches

```bash
# Unit + integration + fixture + property tests.
./gradlew test

# Property tests only — kotest-property, 1000 runs/property by default.
./gradlew test --tests "aether.property.*"

# Bench harness — kotlinx-benchmark over JMH, 11 hot paths,
# JMH-format summary table to stdout.
./gradlew benchmark
```

See `BENCHMARKS.md` for what each case measures and the regression gate
used in CI.
