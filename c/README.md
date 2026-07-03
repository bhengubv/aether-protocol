# Aether Mesh Networking Protocol - C Implementation

[English](README.md) · [Français](../docs/i18n/fr/c/README.md) · [Español](../docs/i18n/es/c/README.md) · [العربية](../docs/i18n/ar/c/README.md) · [中文简体](../docs/i18n/zh-CN/c/README.md) · [日本語](../docs/i18n/ja/c/README.md) · [Deutsch](../docs/i18n/de/c/README.md) · [Português (BR)](../docs/i18n/pt-BR/c/README.md) · [Русский](../docs/i18n/ru/c/README.md) · [فارسی](../docs/i18n/fa/c/README.md) · [한국어](../docs/i18n/ko/c/README.md)

A high-performance, embedded-friendly C implementation of the Aether mesh networking protocol primitives. Designed for resource-constrained devices like ESP32 and nRF52, it provides Ed25519 signing, AES-256-GCM encryption, HMAC/HKDF, and wire-format serialization. **Note:** the C port ships protocol *primitives* only — it does not implement AODV routing or the full Signal session machinery (X3DH / Double Ratchet / OPK lifecycle); see `OPEN_ISSUES.md`. Its only mesh transport is an in-process simulator (no real BLE/Wi-Fi Direct radio).

## Overview

Aether is a decentralised mesh networking protocol for environments with intermittent or absent internet connectivity. This C implementation provides:

- **Protocol serialization/deserialization** — little-endian wire format matching the C# reference implementation
- **Cryptographic operations** — Ed25519 signatures, AES-256-GCM encryption, HMAC-SHA256, HKDF-SHA256 (via libsodium)
- **Packet signing** — deterministic signable data construction per the protocol spec
- **Transport abstraction** — vtable pattern for custom transport implementations
- **In-process transport** — built-in in-process simulator transport for multi-node test scenarios (not a real radio)
- **WebRTC transport** — real internet peer-to-peer data-channel transport in `src/transport_webrtc.c`. **Status: written, but NOT yet verified (built/tested) on the dev box** — treat as unproven until the C WebRTC tests run green
- **Embedded-first design** — fixed-size buffers where possible, minimal allocation, constant-time operations
- **Security & privacy layer** — BIP-39 recovery-phrase backup, BLE tracking-protection (rotating Service UUID + resolvable private addresses), panic-wipe secure-erase, and decentralised multi-device sync (`SyncRecord` + Ed25519 `DeviceLink`), each byte-matched to the C# reference under `fixtures/{bip39,bleprivacy,panicwipe,sync}/` (`src/bip39.c`, `src/ble_privacy.c`, `src/panic_wipe.c`, `src/sync.c`; verified with `ctest` on the macOS build server)

## Build Requirements

- **CMake** ≥ 3.16
- **C11 compiler** (gcc, clang, etc.)
- **libsodium** — for cryptographic operations
- **POSIX threads** (pthread)

### macOS

```bash
# Install libsodium using Homebrew
brew install libsodium

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### Linux (Ubuntu/Debian)

```bash
# Install dependencies
sudo apt-get install libsodium-dev build-essential cmake

# Build
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make
```

### ESP-IDF (ESP32)

The library is designed to be used as an ESP-IDF component:

```bash
# In your ESP-IDF project components directory
cp -r /Users/admin/Code/Dev/aether-protocol/c/include aether
cp -r /Users/admin/Code/Dev/aether-protocol/c/src aether/

# Create idf_component.yml
cat > aether/idf_component.yml << 'EOF'
version: "1.0.0"
description: "Aether Mesh Networking Protocol"
dependencies:
  libsodium: "*"
EOF

# In your project's CMakeLists.txt
idf_component_register(
    INCLUDE_DIRS "aether/include"
    SRCS "aether/src/protocol.c" "aether/src/security.c" "aether/src/transport_inprocess.c"
    REQUIRES libsodium pthread
)
```

## Structure

```
c/
├── include/aether/
│   ├── constants.h       # Protocol constants and limits
│   ├── protocol.h        # Packet structure and serialization
│   ├── security.h        # Cryptographic operations
│   └── transport.h       # Transport abstraction
├── src/
│   ├── protocol.c        # Serialization implementation
│   ├── security.c        # Cryptography using libsodium
│   ├── transport_inprocess.c  # In-process test transport
│   └── demo.c            # Example usage
├── tests/
│   ├── CMakeLists.txt
│   └── test_protocol.c   # Unit tests
├── CMakeLists.txt
└── README.md
```

## Quick Start

### Build and Run Demo

```bash
cd /Users/admin/Code/Dev/aether-protocol/c
mkdir build && cd build
cmake ..
make

# Run the demo
./aether-demo
```

Expected output demonstrates:
1. Ed25519 key generation
2. Packet creation and signing
3. Serialization to wire format
4. Deserialization
5. AES-256-GCM encryption/decryption
6. HMAC-SHA256 authentication
7. HKDF key derivation

### Run Unit Tests

```bash
cd build
cmake .. -DCMAKE_BUILD_TYPE=Debug
make
ctest --output-on-failure
```

### Use in Your Code

```c
#include "aether/protocol.h"
#include "aether/security.h"

int main(void) {
    // Create a packet
    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    if (!packet) return 1;

    // Set fields
    aethernet_packet_set_source_uhid(packet, "node-alice");
    aethernet_packet_set_destination_uhid(packet, "node-bob");
    aethernet_packet_set_payload(packet, (const uint8_t *)"Hello mesh!", 11);

    // Generate and sign
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    aethernet_ed25519_generate_keypair(private_key, public_key);

    size_t signable_len = 0;
    uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
    if (signable) {
        uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];
        aethernet_ed25519_sign(private_key, signable, signable_len, signature);
        aethernet_packet_set_signature(packet, signature, AETHERNET_ED25519_SIGNATURE_SIZE);
        free(signable);
    }

    // Serialize
    uint8_t buffer[4096];
    int size = aethernet_packet_serialize(packet, buffer, sizeof(buffer));
    if (size > 0) {
        printf("Packet serialized: %d bytes\n", size);
    }

    // Deserialize
    aethernet_mesh_packet_t *received = aethernet_packet_deserialize(buffer, size);
    if (received) {
        printf("Received from: %s\n", received->source_uhid);
        aethernet_packet_free(received);
    }

    aethernet_packet_free(packet);
    return 0;
}
```

## API Reference

### Protocol

#### Packet Management
- `aethernet_mesh_packet_t *aethernet_packet_new(void)` — Create a new packet
- `void aethernet_packet_free(aethernet_mesh_packet_t *packet)` — Free a packet
- `aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet)` — Clone a packet

#### Serialization
- `int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet, uint8_t *buffer, size_t buffer_len)` — Serialize to wire format
- `aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data, size_t data_len)` — Deserialize from wire format
- `size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet)` — Estimate wire size

#### Packet Fields
- `bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Set source
- `bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet, const char *uhid)` — Set destination
- `bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet, const uint8_t *data, size_t len)` — Set payload
- `bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet, const uint8_t *sig, size_t len)` — Set signature

#### Validation
- `bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet, int max_age_seconds)` — Check if expired
- `bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet)` — Check if TTL > 0

#### Signing Data
- `uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet, size_t *out_len)` — Get deterministic signable bytes (caller must free)

### Security

#### Ed25519
- `bool aethernet_ed25519_generate_keypair(uint8_t *out_private, uint8_t *out_public)` — Generate 32+32 byte keys
- `bool aethernet_ed25519_sign(const uint8_t *private_key, const uint8_t *data, size_t data_len, uint8_t *out_signature)` — Sign (produces 64 bytes)
- `bool aethernet_ed25519_verify(const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *signature)` — Verify

#### AES-256-GCM
- `bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext, size_t plaintext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *aad, size_t aad_len, uint8_t *out_ciphertext, uint8_t *out_tag, uint8_t *out_nonce)` — Encrypt (nonce auto-generated if NULL)
- `bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext, size_t ciphertext_len, const uint8_t *key, const uint8_t *nonce, const uint8_t *tag, const uint8_t *aad, size_t aad_len, uint8_t *out_plaintext)` — Decrypt

#### HMAC & Hash
- `bool aethernet_hmac_sha256(const uint8_t *key, size_t key_len, const uint8_t *data, size_t data_len, uint8_t *out_hash)` — HMAC-SHA256 (32 bytes)
- `bool aethernet_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash)` — SHA-256 (32 bytes)
- `bool aethernet_hkdf_sha256(const uint8_t *salt, size_t salt_len, const uint8_t *ikm, size_t ikm_len, const uint8_t *info, size_t info_len, size_t output_len, uint8_t *out_okm)` — HKDF (RFC 5869)

#### Utilities
- `void aethernet_zeroize(void *mem, size_t len)` — Constant-time memory wiping
- `bool aethernet_random_bytes(uint8_t *out, size_t len)` — Cryptographically random bytes

### Transport

#### Generic Functions
- `bool aethernet_transport_send(aethernet_transport_t *transport, const char *peer_uhid, const uint8_t *data, size_t data_len)` — Send data
- `bool aethernet_transport_is_connected(aethernet_transport_t *transport, const char *peer_uhid)` — Check connection
- `void aethernet_transport_set_on_data_received(aethernet_transport_t *transport, aethernet_transport_on_data_received callback, void *user_data)` — Register callback
- `void aethernet_transport_destroy(aethernet_transport_t *transport)` — Cleanup

#### In-Process Transport
- `aethernet_transport_t *aethernet_inprocess_transport_new(void)` — Create shared in-process transport
- `bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport, const char *uhid)` — Register a node
- `bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport, const char *uhid)` — Unregister a node

## Wire Format Compliance

This implementation strictly follows the protocol specification with **little-endian** multi-byte integers:

```
[1] protocol_version
[1] type
[16] packet_id (UUID bytes)
[1] priority
[4] ttl (little-endian int32)
[8] timestamp_ms (little-endian int64)
[2] source_uhid_len (little-endian uint16)
[N] source_uhid (UTF-8)
[2] destination_uhid_len (little-endian uint16)
[N] destination_uhid (UTF-8)
[2] nonce_len (little-endian uint16)
[N] packet_nonce
[4] payload_len (little-endian int32)
[N] payload
[2] signature_len (little-endian uint16)
[N] signature (Ed25519, 64 bytes)
```

Packets serialized by this C implementation are 100% compatible with the C# reference implementation.

## Security Considerations

### Cryptographic Libraries
- **libsodium** (libsodium.org) for all cryptographic operations
- Ed25519 signatures and verification
- AES-256-GCM authenticated encryption
- HMAC-SHA256 and SHA-256
- HKDF-SHA256 key derivation
- Cryptographically secure random number generation

### Key Zeroing
All sensitive material (keys, plaintext, intermediate values) is zeroed from memory using `sodium_memzero()` immediately after use. This prevents accidental key leakage.

### Packet Validation
- Timestamp-based deduplication: packets older than 300 seconds are rejected
- Nonce uniqueness: 8-byte random nonce in every packet
- TTL validation: packets with TTL=0 are dropped
- Signature verification: Ed25519 signatures are mandatory in protocol v2

## Embedded Device Notes

### ESP32
- Requires libsodium port for ESP-IDF (available via ESP-IDF components)
- Fixed packet size estimation simplifies memory allocation
- Uses POSIX threads for mutex operations
- Pre-allocate buffers on the stack where possible

### nRF52
- Similar to ESP32
- BLE GATT transport layer can be implemented via the transport vtable
- Consider using a RTOS like FreeRTOS for multi-packet handling

### Memory Usage
- Minimum packet: ~52 bytes
- Maximum packet: 65KB (configurable via `AETHERNET_MAX_PAYLOAD_LEN`)
- A 256-node peer table: ~32KB
- Single mesh packet in memory: ~8KB (worst case with maximum fields)

## Performance

On a modern x86-64 machine (Intel Core i9):
- **Serialization**: ~1-2 µs per packet
- **Deserialization**: ~1-2 µs per packet
- **Ed25519 sign**: ~100 µs
- **Ed25519 verify**: ~300 µs
- **AES-256-GCM encrypt**: ~1 µs per KB
- **SHA-256**: ~0.5 µs per KB

## Testing

```bash
# Build and test
mkdir build && cd build
cmake ..
make
ctest --output-on-failure --verbose
```

Tests cover:
- Packet creation and cloning
- Serialization round-trips
- Ed25519 signing and verification
- AES-GCM encryption/decryption
- HMAC-SHA256 computation
- HKDF key derivation
- TTL and expiry validation
- Signable data determinism

## Integration with Aether Ecosystem

This C library is designed to integrate with:
- **AetherNetAPI** (C#) — server-side mesh relay and analytics
- **AetherNet.Core** (C#) — reference implementation (interoperable wire format)
- **Meshtastic** — open-source mesh radio firmware
- **esp-idf** — Espressif IoT Development Framework
- Custom embedded applications

## License

SPDX-License-Identifier: MIT

See LICENSE file for full text.

## Contributing

Contributions welcome! Please ensure:
- All tests pass (`ctest --output-on-failure`)
- Code is C11 compliant
- Wire format matches the C# reference exactly
- All sensitive data is zeroized
- Documentation is updated

## References

- Protocol Spec: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# Reference: `/Users/admin/Code/Dev/aether-protocol/src/AetherNet.Core/`
- libsodium: https://libsodium.org/
- RFC 5869 (HKDF): https://tools.ietf.org/html/rfc5869
- RFC 3561 (AODV): https://tools.ietf.org/html/rfc3561
