// SPDX-License-Identifier: MIT
//
// PeerId derivation — C implementation.
//
// Derives a libp2p PeerID from a node's Ed25519 public key — the bridge between
// an AetherNet identity and the global libp2p relay / DHT used by the
// decentralised relay layer.
//
// Because AetherNet and libp2p both key identity off the same Ed25519 public
// key, the PeerID is a pure, deterministic function of that key — no lookup
// table, no network. A node can compute its own PeerID (to announce on the
// libp2p DHT) and any peer's PeerID (to dial it) from the public key alone.
//
// Encoding (byte-identical across every SDK language):
//   1. protobuf PublicKey  = 08 01 (Type=Ed25519) 12 20 (Data,len=32) + key  (36 bytes)
//   2. identity multihash  = 00 (identity code) 24 (len=36) + protobuf        (38 bytes)
//   3. PeerID string       = base58btc(multihash) with no multibase prefix    (12D3Koo…)
//
// Verified byte-for-byte against real js-libp2p output; see fixtures/peerid/.
// Cross-language stable: the same encoding is implemented in C#, Go, Python,
// TypeScript, Rust, Kotlin, and Swift.

#ifndef AETHERNET_PEER_ID_H
#define AETHERNET_PEER_ID_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdbool.h>
#include <stdint.h>

/// Length in bytes of a raw Ed25519 public key.
#define AETHERNET_ED25519_PUBLIC_KEY_LENGTH 32

/// Derive the libp2p PeerID string (e.g. "12D3Koo…") for a 32-byte Ed25519
/// public key.
///
/// @param pubkey 32-byte raw Ed25519 public key (bytes treated as unsigned).
/// @param out    Caller-provided buffer that receives the NUL-terminated PeerID
///               string. A 38-byte identity multihash base58-encodes to at most
///               53 characters, so a 64-byte buffer always suffices.
/// @return       true on success; false if pubkey or out is NULL. (The 32-byte
///               length is a contract of the array parameter; callers that hold
///               the key as a pointer + length must check the length first.)
bool aethernet_peer_id_from_ed25519(const uint8_t pubkey[32], char out[64]);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PEER_ID_H
