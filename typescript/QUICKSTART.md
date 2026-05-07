<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — TypeScript Quickstart

A 5-minute wiring guide for adopting the TypeScript implementation.
Covers identity setup, pre-key bundle exchange, encrypt / decrypt,
persistence across restarts, and encryption-at-rest.

## 1. Install

The implementation lives in this repository's `typescript/` subtree and
publishes the `@aether-protocol/core` package. For a local dev install
from a checkout:

```bash
cd typescript
npm install
```

Runtime dependencies are `tweetnacl` (Ed25519), `@noble/hashes` (HKDF),
and `uuid`. X25519 + AES-GCM come from Node's built-in `crypto` so no
third-party crypto library is required for the curve itself. Node 20+
(or any runtime supporting the WebCrypto / Node crypto X25519 KEM).

## 2. Identity setup

Each node has two long-term identity keypairs — Ed25519 for signing
pre-key bundles, X25519 for X3DH ECDH. The service generates them on
first construction and reloads them from the configured pre-key store
on subsequent constructions, so bundles published to peers stay valid
across process restarts.

```ts
import { SignalProtocol } from "@aether-protocol/core";

const alice = new SignalProtocol();
alice.setLocalUhid("alice-uhid-0001");
await alice.ready(); // optional — public methods await internally
```

## 3. Generate and publish a pre-key bundle

A pre-key bundle is what other nodes need to initiate a Signal session
with you. It contains your two identity public keys, your active
signed pre-key (with Ed25519 signature), and one OPK from the pool.

```ts
const bundle = await alice.generatePreKeyBundle("alice-uhid-0001");
// Publish bundle.identityKey, bundle.signedPreKey,
// bundle.signedPreKeySignature, bundle.preKey, and bundle.preKeyId
// wherever your peers can fetch them.
```

The OPK pool is topped up to `opkPoolSize` (default 100) on every
bundle generation. Each OPK is single-use — the responder consumes it
during X3DH and never hands the same id out twice.

## 4. Process a peer's bundle (initiator side)

When you fetch Bob's bundle, hand it to `processPreKeyBundle`. This
runs X3DH (4× X25519 + HKDF) and stages the Double-Ratchet state for
your first encrypt to Bob.

```ts
const bobBundle = await fetchBobsBundle(); // your transport / discovery
await alice.processPreKeyBundle(bobBundle);
```

## 5. Encrypt and decrypt

The first message Alice sends to Bob carries her X3DH inputs as a
PreKey message (`messageType === 1`); Bob runs his side of X3DH on
receive. Subsequent messages are normal Double-Ratchet messages.

```ts
const payload = await alice.encrypt("bob-uhid-0001", new TextEncoder().encode("hello, mesh"));
// Wire-format the payload via your transport — fields are Uint8Array,
// except senderUhid (string), counter (number), and messageType (number).

const plaintext = await bob.decrypt("alice-uhid-0001", payload);
console.log(new TextDecoder().decode(plaintext)); // "hello, mesh"
```

## 6. Persistent state

Wire `KeyValueSignalSessionStore` and `KeyValuePreKeyStore` over any
`KeyValueStore` to survive restarts. Sessions, identity, SPK history,
and the OPK pool all snapshot after every mutation.

```ts
import {
  SignalProtocol,
  KeyValueSignalSessionStore,
  KeyValuePreKeyStore,
  FileSystemKeyValueStore,
} from "@aether-protocol/core";

const kv = new FileSystemKeyValueStore("./aether-data");
const sessions = new KeyValueSignalSessionStore(kv);
const preKeys = new KeyValuePreKeyStore(kv);

const service = new SignalProtocol({
  sessionStore: sessions,
  preKeyStore: preKeys,
});
await service.ready();
```

Tests can swap `FileSystemKeyValueStore` for `InMemoryKeyValueStore` —
the round-trip path is identical so the production code path is
exercised either way.

## 7. Encryption-at-rest

Compose `EncryptedKeyValueStore` over the inner KV to AES-256-GCM
every value before it touches disk. Keys are passed through unchanged
so list / range queries continue to work.

```ts
import {
  EncryptedKeyValueStore,
  StaticDataAtRestKeyProvider,
  FileSystemKeyValueStore,
  KeyValueSignalSessionStore,
} from "@aether-protocol/core";

const masterKey = new Uint8Array(32); // 32 random bytes from your KMS
const keyProvider = new StaticDataAtRestKeyProvider(1, new Map([[1, masterKey]]));
const inner = new FileSystemKeyValueStore("./aether-data");
const secure = new EncryptedKeyValueStore(inner, keyProvider);
const sessions = new KeyValueSignalSessionStore(secure);
```

The wire format is byte-identical to the C# / Go / Python / Rust
references — a TypeScript host can decrypt blobs written by any of
them given the same key material and version registry.

## 8. Cross-language interop

Wire compatibility across the eight implementation families is anchored
by `fixtures/` at the repository root. The TypeScript verifier is
`tests/fixtures.test.ts` — it loads `fixtures/inputs.json` and asserts
that the TypeScript `PacketSerializer` produces byte-identical output to
the canonical C# expected. Signal-protocol fixtures under
`fixtures/signal/` cover X3DH + double-ratchet KDF outputs.

If the verifier passes (`npx tsx --test tests/fixtures.test.ts`), your
TypeScript host can talk to a C# / Go / Python / Rust / Kotlin / Swift
/ Java host on the wire.

## 9. Running tests and benches

```bash
# Per-file test runner (the directory-level npm test has a tsx
# resolution issue with the bundled --test runner on Windows).
for f in tests/*.test.ts; do npx tsx --test "$f"; done

# Fuzz tests — fast-check, 1000 runs/property by default.
npx tsx --test tests/fuzz.test.ts

# Bench harness — tinybench, 11 hot paths, markdown table to stdout.
npm run bench
```

See `BENCHMARKS.md` for what each case measures and the regression
gate used in CI.
