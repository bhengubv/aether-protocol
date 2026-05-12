<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Go Quickstart

A 5-minute wiring guide for adopting the Go implementation. Covers
identity setup, pre-key bundle exchange, encrypt / decrypt, persistence
across restarts, and encryption-at-rest.

## 1. Install

The implementation lives in this repository's `go/` subtree and
publishes the `github.com/bhengubv/aether-protocol/go` module.
For a local dev install from a checkout:

```bash
cd go
go mod download
```

To pull it into another module:

```bash
go get github.com/bhengubv/aether-protocol/go
```

Runtime dependencies are `golang.org/x/crypto` (HKDF) and
`github.com/google/uuid`. X25519, Ed25519, AES-GCM, and SHA-256 come
from the Go stdlib (`crypto/ecdh`, `crypto/ed25519`, `crypto/aes`,
`crypto/cipher`). Go 1.22+.

## 2. Identity setup

Each node has two long-term identity keypairs — Ed25519 for signing
pre-key bundles, X25519 for X3DH ECDH. The service generates them on
first construction and reloads them from the configured pre-key store
on subsequent constructions, so bundles published to peers stay valid
across process restarts.

```go
import "github.com/bhengubv/aether-protocol/go/security"

alice, err := security.NewSignalProtocolService()
if err != nil {
    log.Fatal(err)
}
alice.SetLocalUhid("alice-uhid-0001")
```

## 3. Generate and publish a pre-key bundle

A pre-key bundle is what other nodes need to initiate a Signal session
with you. It contains your two identity public keys, your active
signed pre-key (with Ed25519 signature), and one OPK from the pool.

```go
bundle, err := alice.GeneratePreKeyBundle("alice-uhid-0001")
if err != nil {
    log.Fatal(err)
}
// Publish bundle.IdentityKey, bundle.IdentityKeyX25519,
// bundle.SignedPreKey, bundle.SignedPreKeySignature, bundle.PreKey,
// and bundle.PreKeyID wherever your peers can fetch them.
```

The OPK pool is topped up to the configured size (default 100,
overridable via `security.WithOpkPoolSize`) on every bundle generation.
Each OPK is single-use — the responder consumes it during X3DH and never
hands the same id out twice.

## 4. Process a peer's bundle (initiator side)

When you fetch Bob's bundle, hand it to `ProcessPreKeyBundle`. This
runs X3DH (4x X25519 + HKDF) and stages the Double-Ratchet state for
your first encrypt to Bob.

```go
bobBundle := fetchBobsBundle() // your transport / discovery
if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
    log.Fatal(err)
}
```

## 5. Encrypt and decrypt

The first message Alice sends to Bob carries her X3DH inputs as a
PreKey message (`MessageType == security.MessageTypePreKey`); Bob runs
his side of X3DH on receive. Subsequent messages are normal
Double-Ratchet messages.

```go
payload, err := alice.Encrypt("bob-uhid-0001", []byte("hello, mesh"))
if err != nil {
    log.Fatal(err)
}
// Wire-format the payload via your transport — fields are []byte,
// except SenderUhid (string), Counter (int32), and MessageType (int).

plaintext, err := bob.Decrypt("alice-uhid-0001", payload)
if err != nil {
    log.Fatal(err)
}
// plaintext == []byte("hello, mesh")
```

## 6. Persistent state

Wire `NewKVSessionStore` and `NewKVPreKeyStore` over any
`storage.IKeyValueStore` to survive restarts. Sessions, identity, SPK
history, and the OPK pool all snapshot after every mutation.

```go
import (
    "github.com/bhengubv/aether-protocol/go/security"
    "github.com/bhengubv/aether-protocol/go/storage"
)

// Volatile (tests) — storage.NewInMemoryKeyValueStore().
// Durable — storage.NewFileSystemKeyValueStore("./aether-data", "node1").
kv, err := storage.NewFileSystemKeyValueStore("./aether-data", "node1")
if err != nil {
    log.Fatal(err)
}
sessions := security.NewKVSessionStore(kv)
preKeys := security.NewKVPreKeyStore(kv)

service, err := security.NewSignalProtocolService(
    security.WithSessionStore(sessions),
    security.WithPreKeyStore(preKeys),
)
```

## 7. Encryption-at-rest

Compose `storage.NewEncryptedKeyValueStore` over the inner KV to
AES-256-GCM every value before it touches disk. Keys are passed through
unchanged so list / range queries continue to work.

```go
import "github.com/bhengubv/aether-protocol/go/storage"

masterKey := make([]byte, 32) // 32 bytes from your KMS
provider, err := storage.NewStaticDataAtRestKeyProvider(masterKey)
if err != nil {
    log.Fatal(err)
}
inner, err := storage.NewFileSystemKeyValueStore("./aether-data", "node1")
if err != nil {
    log.Fatal(err)
}
secure := storage.NewEncryptedKeyValueStore(inner, provider, nil)
sessions := security.NewKVSessionStore(secure)
```

The wire format is byte-identical to the C# / Python / TypeScript /
Rust references — a Go host can decrypt blobs written by any of them
given the same key material and version registry.

## 8. Cross-language interop

Wire compatibility across the eight implementation families is anchored
by `fixtures/` at the repository root. The Go verifier is
`protocol/fixture_test.go` — it loads `fixtures/inputs.json` and
asserts that the Go `PacketSerializer` produces byte-identical output
to the canonical C# expected. Signal-protocol fixtures under
`fixtures/signal/` cover X3DH + double-ratchet KDF outputs and are
verified by `security/signal_fixture_test.go`.

If the verifiers pass (`go test ./protocol/... ./security/...`), your
Go host can talk to a C# / Python / TypeScript / Rust / Kotlin / Swift
/ Java host on the wire.

## 9. Running tests, benches, and fuzz

```bash
# Full test suite.
go test ./...

# Bench harness — 11 hot paths, mirrors the Python / TypeScript / C suites.
go test -bench=. ./bench/...

# Fuzz harnesses — short smoke run.
go test -fuzz "FuzzDeserialize$" -fuzztime 30s ./protocol/...
```

See [`BENCHMARKS.md`](BENCHMARKS.md) for what each bench measures and
[`FUZZING.md`](FUZZING.md) for the fuzz target inventory.
