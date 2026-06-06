<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Python Quickstart

A 5-minute wiring guide for adopting the Python implementation. Covers
identity setup, pre-key bundle exchange, encrypt / decrypt, and
persistence across restarts.

## 1. Install

The implementation lives in this repository's `python/` subtree and
publishes the `aether` package. For a local dev install from a checkout:

```bash
cd python
pip install -e .[dev]
```

Runtime dependencies are `pynacl` (Ed25519) and `cryptography` (X25519
+ AES-GCM + HKDF). Python 3.10+.

## 2. Identity setup

Each node has two long-term identity keypairs — Ed25519 for signing
pre-key bundles, X25519 for X3DH ECDH. The service generates them on
first construction and reloads them from the configured pre-key store
on subsequent constructions, so bundles published to peers stay valid
across process restarts.

```python
import asyncio
from aethermesh.security.signal_protocol import SignalProtocolService

async def main() -> None:
    alice = SignalProtocolService()
    alice.set_local_uhid("alice-uhid-0001")
```

## 3. Generate and publish a pre-key bundle

A pre-key bundle is what other nodes need to initiate a Signal session
with you. It contains your two identity public keys, your active signed
pre-key (with Ed25519 signature), and one OPK from the pool.

```python
    bundle = await alice.generate_pre_key_bundle("alice-uhid-0001")
    # Publish bundle.identity_key, bundle.signed_pre_key,
    # bundle.signed_pre_key_signature, bundle.pre_key, and
    # bundle.pre_key_id wherever your peers can fetch them.
```

The OPK pool is topped up to `opk_pool_size` (default 100) on every
bundle generation. Each OPK is single-use — the responder consumes it
during X3DH and never hands the same id out twice.

## 4. Process a peer's bundle (initiator side)

When you fetch Bob's bundle, hand it to `process_pre_key_bundle`. This
runs X3DH (4x X25519 + HKDF) and stages the Double-Ratchet state for
your first encrypt to Bob.

```python
    bob_bundle = fetch_bobs_bundle()  # your transport / discovery
    await alice.process_pre_key_bundle(bob_bundle)
```

## 5. Encrypt and decrypt

The first message Alice sends to Bob carries her X3DH inputs as a
PreKey message (`message_type == 1`); Bob runs his side of X3DH on
receive. Subsequent messages are normal Double-Ratchet messages.

```python
    payload = await alice.encrypt("bob-uhid-0001", b"hello, mesh")
    # Wire-format the payload via your transport — fields are bytes,
    # except sender_uhid (string), counter (int), and message_type (int).

    plaintext = await bob.decrypt("alice-uhid-0001", payload)
    assert plaintext == b"hello, mesh"
```

## 6. Persistent state

Wire `KeyValueSignalSessionStore` and `KeyValuePreKeyStore` over any
`KeyValueStore` to survive restarts. Sessions, identity, SPK history,
and the OPK pool all snapshot after every mutation.

```python
from aethermesh.storage.in_memory_kv import InMemoryKeyValueStore
from aethermesh.storage.filesystem_kv import FileSystemKeyValueStore
from aethermesh.security.session_store import KeyValueSignalSessionStore
from aethermesh.security.pre_key_store import KeyValuePreKeyStore

# Volatile (tests) — InMemoryKeyValueStore.
# Durable — FileSystemKeyValueStore(root_dir="./aether-data").
kv = FileSystemKeyValueStore("./aether-data")
sessions = KeyValueSignalSessionStore(kv)
pre_keys = KeyValuePreKeyStore(kv)

service = SignalProtocolService(
    session_store=sessions,
    pre_key_store=pre_keys,
)
```

## 7. Encryption-at-rest

Compose `EncryptedKeyValueStore` over the inner KV to AES-256-GCM
every value before it touches disk. Keys are passed through unchanged
so list / range queries continue to work.

```python
from aethermesh.storage.encrypted_kv import EncryptedKeyValueStore
from aethermesh.storage.static_key_provider import StaticDataAtRestKeyProvider

key_provider = StaticDataAtRestKeyProvider(
    current_version=1,
    keys={1: master_key_bytes},  # 32 bytes
)
inner = FileSystemKeyValueStore("./aether-data")
secure = EncryptedKeyValueStore(inner, key_provider)
sessions = KeyValueSignalSessionStore(secure)
```

The wire format is byte-identical to the C# / Go / TypeScript / Rust
references — a Python host can decrypt blobs written by any of them
given the same key material and version registry.

## 8. Cross-language interop

Wire compatibility across the eight implementation families is anchored
by `fixtures/` at the repository root. The Python verifier is
`tests/test_fixtures.py` — it loads `fixtures/inputs.json` and asserts
that the Python `PacketSerializer` produces byte-identical output to
the canonical C# expected. Signal-protocol fixtures under
`fixtures/signal/` cover X3DH + double-ratchet KDF outputs.

If the verifier passes, your Python host can talk to a C# / Go /
TypeScript / Rust / Kotlin / Swift / Java host on the wire.
