# Aether Mesh Networking Protocol - Python Implementation

[English](README.md) · [Français](../docs/i18n/fr/python/README.md) · [Español](../docs/i18n/es/python/README.md) · [العربية](../docs/i18n/ar/python/README.md) · [中文简体](../docs/i18n/zh-CN/python/README.md) · [日本語](../docs/i18n/ja/python/README.md) · [Deutsch](../docs/i18n/de/python/README.md) · [Português (BR)](../docs/i18n/pt-BR/python/README.md) · [Русский](../docs/i18n/ru/python/README.md) · [فارسی](../docs/i18n/fa/python/README.md) · [한국어](../docs/i18n/ko/python/README.md)

A Python implementation of the Aether mesh networking protocol, providing wire-compatible cryptographic operations with the C# reference implementation.

## Overview

Aether is a decentralized mesh networking protocol designed for environments with intermittent or absent internet connectivity. This Python package provides:

- **Ed25519 Signing**: Key generation, signing, and verification using PyNaCl
- **Signal Protocol X3DH**: Asynchronous key exchange with ECDH P-256
- **AES-256-GCM Encryption**: Per-message symmetric encryption with 12-byte nonces
- **HKDF-SHA256 Key Derivation**: RFC 5869 compliant key derivation with context-specific info strings
- **Symmetric Ratchet**: HMAC-SHA256 based message key derivation with forward secrecy
- **Packet Serialization**: Little-endian binary wire format matching C# implementation
- **Replay Attack Prevention**: Nonce-based deduplication with 5-minute TTL
- **In-Process Transport**: Mock/in-process transport for testing mesh communication (a simulator; there is no real BLE/Wi-Fi Direct radio in the Python port)
- **WebRTC Transport**: Real internet peer-to-peer data-channel transport in `aethernet/transport/webrtc/`. **Status: written, but NOT yet verified (built/tested) on the dev box** — treat as unproven until the Python WebRTC tests run green

## Installation

### From PyPI (when published)
```bash
pip install aether-protocol
```

### From Source
```bash
cd /Users/admin/Code/Dev/aether-protocol/python
pip install -e .
```

### Development Dependencies
```bash
pip install -e ".[dev]"
```

## Quick Start

```python
import asyncio
from aethernet.security.ed25519_service import Ed25519SigningService
from aethernet.security.signal_protocol import SignalProtocolService
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Generate Ed25519 keys
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign a message
message = b"Hello, Aether Mesh!"
signature = Ed25519SigningService.sign(private_key, message)

# Verify the signature
is_valid = Ed25519SigningService.verify(public_key, message, signature)
print(f"Signature valid: {is_valid}")
```

## Architecture

### Package Structure

```
aether/
├── __init__.py              # Package exports
├── constants.py             # Protocol constants
├── models.py                # Data models (AetherNetNode, PeerInfo, RouteEntry)
├── protocol/
│   ├── __init__.py
│   ├── mesh_packet.py       # MeshPacket and PacketType definitions
│   └── serializer.py        # Binary serialization/deserialization
├── security/
│   ├── __init__.py
│   ├── ed25519_service.py   # Ed25519 signing and verification
│   ├── signal_protocol.py   # Signal Protocol X3DH + symmetric ratchet
│   └── packet_signing.py    # Packet signing with replay detection
└── transport/
    ├── __init__.py
    ├── transport_service.py  # Abstract transport base class
    └── in_process.py        # In-memory transport for testing
```

## Key Features

### 1. Ed25519 Signing Service

Uses PyNaCl (libsodium) for cryptographic operations:

```python
from aethernet.security.ed25519_service import Ed25519SigningService

# Generate a key pair
private_key, public_key = Ed25519SigningService.generate_keypair()

# Sign data
signature = Ed25519SigningService.sign(private_key, data)

# Verify a signature
is_valid = Ed25519SigningService.verify(public_key, data, signature)
```

**Key Sizes:**
- Private key: 32 bytes (Ed25519 seed)
- Public key: 32 bytes (Ed25519 point)
- Signature: 64 bytes

### 2. Signal Protocol

Implements X3DH key exchange with symmetric ratchet for forward secrecy:

```python
from aethernet.security.signal_protocol import SignalProtocolService

# Create protocol instances
alice_signal = SignalProtocolService()
bob_signal = SignalProtocolService()

# Bob publishes a pre-key bundle
bob_bundle = await bob_signal.generate_pre_key_bundle("bob-001")

# Alice processes the bundle to establish a session
await alice_signal.process_pre_key_bundle(bob_bundle)

# Alice encrypts a message
plaintext = b"Secret message"
encrypted = await alice_signal.encrypt("bob-001", plaintext)

# Bob must also process Alice's bundle for bidirectional communication
alice_bundle = await alice_signal.generate_pre_key_bundle("alice-001")
await bob_signal.process_pre_key_bundle(alice_bundle)

# Bob decrypts the message
decrypted = await bob_signal.decrypt("alice-001", encrypted)
```

**Key Derivation:**
- Uses HKDF-SHA256 with salt: `"AetherNetSignal"`
- Root key info: `"aether-root-v1"`
- Send chain info: `"aether-chain-send-v1"`
- Receive chain info: `"aether-chain-recv-v1"`

**Symmetric Ratchet:**
- Uses HMAC-SHA256 with the chain key
- Derives new message keys and advances the chain with each message
- Supports up to 1000 skipped keys for out-of-order delivery
- Per-message encryption: AES-256-GCM with random 12-byte nonce

### 3. Packet Serialization

Wire-compatible binary format matching the C# implementation:

```python
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.protocol.serializer import PacketSerializer

# Create a packet
packet = MeshPacket(
    type=PacketType.Data,
    source_uhid="node-alice",
    destination_uhid="node-bob",
    ttl=7,
    priority=0,
    payload=b"Message payload"
)

# Serialize to binary
binary = PacketSerializer.serialize(packet)

# Deserialize from binary
decoded_packet = PacketSerializer.deserialize(binary)
```

**Wire Format (Little-Endian):**
- Protocol version: 1 byte
- Packet type: 1 byte
- Packet ID: 16 bytes (UUID)
- Priority: 1 byte
- TTL: 4 bytes (int32)
- TimestampMs: 8 bytes (int64)
- SourceUhid length: 2 bytes + UTF-8 data
- DestinationUhid length: 2 bytes + UTF-8 data
- PacketNonce length: 2 bytes + data
- Payload length: 4 bytes + data
- Signature length: 2 bytes + data

### 4. Packet Signing

Signs packets using Ed25519 and detects replay attacks:

```python
from aethernet.security.packet_signing import PacketSigningService

signing_service = PacketSigningService()

# Sign a packet
signing_service.sign_packet(packet, private_key)

# Verify a packet (also checks for replays)
is_valid = signing_service.verify_packet(packet, public_key)
```

**Signable Data:**
Per protocol spec section 2.3, the signature covers:
- PacketNonce (8 bytes)
- TimestampMs (8 bytes, little-endian int64)
- Type (4 bytes, little-endian int32)
- SourceUhid (length + UTF-8)
- DestinationUhid (length + UTF-8)
- SHA-256(Payload) (32 bytes)
- Ttl (4 bytes, little-endian int32)
- Priority (4 bytes, little-endian int32)

**Replay Prevention:**
- Maintains cache of seen (sender_uhid, nonce) pairs
- 5-minute TTL per cache entry
- Automatic cleanup every 60 seconds

### 5. Transport Services

Abstract base class for physical transports (BLE, Wi-Fi Direct, etc.):

```python
from aethernet.transport.in_process import InProcessTransport

# Create in-process transport instances
alice_transport = InProcessTransport("alice-001")
bob_transport = InProcessTransport("bob-001")

# Register callback for incoming messages
def on_message(sender: str, data: bytes):
    print(f"Received from {sender}: {len(data)} bytes")

bob_transport.on_data_received(on_message)

# Send a message
await alice_transport.send_async("bob-001", b"Hello Bob!")
```

**InProcessTransport Features:**
- Class-level global registry of nodes
- Thread-safe with threading.Lock
- Perfect for testing and local mesh simulation
- Properties: name, is_available, max_bandwidth_bps, max_range_meters, power_cost_relative, max_concurrent_peers

## Constants Reference

All protocol constants are defined in `aether/constants.py`:

### Cryptography
- `ED25519_PRIVATE_KEY_SIZE`: 32 bytes
- `ED25519_PUBLIC_KEY_SIZE`: 32 bytes
- `ED25519_SIGNATURE_SIZE`: 64 bytes
- `AES_GCM_NONCE_SIZE`: 12 bytes
- `AES_GCM_TAG_SIZE`: 16 bytes
- `MAX_SKIPPED_KEYS`: 1000

### Routing
- `DEFAULT_TTL`: 7
- `SOS_TTL`: 15
- `ROUTE_TIMEOUT_MS`: 5000
- `ROUTE_EXPIRY_SECONDS`: 300

### DTN Store-and-Forward
- `DTN_BUNDLE_TTL_HOURS`: 72
- `DTN_MAX_COPIES`: 3
- `DTN_MAX_BUNDLES_PER_NODE`: 50
- `DTN_SCAN_INTERVAL_SECONDS`: 60

(See `constants.py` for full list)

## Running the Demo

Demonstrates all major features with colorful output:

```bash
cd /Users/admin/Code/Dev/aether-protocol/python
python3 demo.py
```

Demo covers:
1. Ed25519 key generation and signing
2. Node creation with AetherNetNode
3. Signal Protocol X3DH key exchange
4. Message encryption and decryption
5. Packet serialization/deserialization
6. Packet signing and replay attack detection
7. In-process transport communication
8. Complete end-to-end encryption workflow

## Dependencies

### Runtime
- `pynacl>=1.5.0` - Ed25519 signing via libsodium
- `cryptography>=41.0.0` - ECDH P-256, HKDF-SHA256, AES-256-GCM, HMAC-SHA256

### Development
- `pytest>=7.4.0` - Testing framework
- `pytest-asyncio>=0.21.0` - Async test support
- `black>=23.0.0` - Code formatting
- `mypy>=1.5.0` - Static type checking
- `ruff>=0.1.0` - Linting

## Compatibility

**Python Version:** 3.10+

**Platform:** Cross-platform (Windows, macOS, Linux)

**Cryptographic Backend:** Uses system libsodium and cryptography library backends, ensuring consistent behavior across platforms.

## Protocol References

- **AODV Routing:** RFC 3561
- **X3DH Key Agreement:** Signal Foundation, November 2016
- **Double Ratchet:** Signal Foundation, November 2016
- **HKDF:** RFC 5869 (HMAC-based Extract-and-Expand)
- **AES-GCM:** NIST SP 800-38D
- **Ed25519:** DJB et al., 2012

## Security Considerations

### Key Zeroing
Intermediate cryptographic material is zeroed after use:
- Shared secrets from ECDH
- Message keys from the symmetric ratchet
- Derived key material in the establishment context

In Python, true in-place memory zeroing is limited, but sensitive data is cleared from variable scope immediately after use.

### Threat Model
Aether assumes:
- Passive eavesdropping on BLE/Wi-Fi
- Active packet injection and replay
- Sybil attacks via fake node creation
- Selective denial of service

Protections include:
- **Confidentiality:** AES-256-GCM per-message keys
- **Integrity:** Ed25519 packet signatures
- **Replay Prevention:** Nonce-based deduplication
- **Forward Secrecy:** Symmetric ratchet with per-message keys
- **Route Authentication:** Signed Route Replies

### Limitations
- Out-of-order message delivery is supported up to 1000 messages
- Messages beyond the gap are rejected
- BLE addresses rotate every 15 minutes (not implemented in Python)
- P-256 to Ed25519 migration window is 30 days (fallback not implemented yet)

## Testing

Run the test suite:

```bash
pytest -v
pytest --asyncio-mode=auto
```

## License

MIT License - See LICENSE file for details

## Contributing

To contribute improvements:

1. Ensure code follows PEP 8 style (use `black` for formatting)
2. Add type hints to all functions
3. Include docstrings for public APIs
4. Run `mypy` for type checking
5. Add tests for new features

## References

- Aether Protocol Spec: `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
- C# Reference Implementation: `/Users/admin/Code/Dev/aether-protocol/src/`
- The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.: https://thegeeknetwork.dev
