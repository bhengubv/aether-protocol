# Aether Mesh Networking Protocol Specification

**Version:** 2.0
**Status:** Draft
**Date:** 2026-03-15
**Authors:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

---

## Table of Contents

1. [Abstract](#1-abstract)
2. [Packet Format](#2-packet-format)
3. [Routing Algorithm](#3-routing-algorithm)
4. [Key Exchange](#4-key-exchange)
5. [Transport Layer Requirements](#5-transport-layer-requirements)
6. [Discovery Protocol](#6-discovery-protocol)
7. [Security Model](#7-security-model)
8. [SOS Broadcast](#8-sos-broadcast)
9. [DTN Store-and-Forward](#9-dtn-store-and-forward)

---

## 1. Abstract

Aether is a decentralised mesh networking protocol designed for environments with intermittent or absent internet connectivity. It provides multi-hop packet routing over heterogeneous short-range transports (Bluetooth Low Energy, Wi-Fi Direct, NearLink), end-to-end encryption using an X3DH-derived key agreement with a symmetric ratchet, delay-tolerant store-and-forward delivery, and an emergency SOS flood mechanism. The protocol is transport-agnostic: any physical layer that can send and receive byte arrays between peers is a valid Aether transport. Nodes are identified by Universal Hardware Identifiers (UHIDs) and authenticated via Ed25519 identity keys. Aether is intended as a universal network layer -- every application in the ecosystem registers Aether services, and nodes without internet connectivity reach the wider network through gateway peers that bridge mesh traffic to the internet.

---

## 2. Packet Format

### 2.1. MeshPacket Structure

Every Aether message is encapsulated in a `MeshPacket`. The logical fields are:

| Field            | Type                     | Size                  | Description |
|------------------|--------------------------|-----------------------|-------------|
| PacketNonce      | bytes                    | 8 bytes               | Cryptographically random nonce for replay prevention |
| TimestampMs      | uint64 (big-endian)      | 8 bytes               | Unix epoch milliseconds (UTC) |
| ProtocolVersion  | uint8                    | 1 byte                | `1` = unsigned (legacy), `2` = signed (current) |
| Type             | uint8                    | 1 byte                | Packet type enumeration (see Section 2.3) |
| Ttl              | uint8                    | 1 byte                | Time-to-live, decremented at each hop |
| Priority         | uint8                    | 1 byte                | Priority level (0 = normal, 999 = SOS) |
| SourceUhid       | length-prefixed UTF-8    | 4 + N bytes           | Sender's UHID; 4-byte little-endian length prefix |
| DestinationUhid  | length-prefixed UTF-8    | 4 + N bytes           | Recipient's UHID; empty string for broadcast |
| Payload          | length-prefixed bytes    | 4 + N bytes           | Application data; 4-byte little-endian length prefix |
| Signature        | length-prefixed bytes    | 4 + N bytes           | Ed25519 signature over signable data (see Section 2.2) |
| Id               | UUID                     | 16 bytes              | Packet identifier for deduplication |

### 2.2. Wire Format Diagram

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       PacketNonce (bytes 0-3)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       PacketNonce (bytes 4-7)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       TimestampMs (bytes 0-3)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       TimestampMs (bytes 4-7)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type     | TTL      | Priority   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  SourceUhid Length (4 bytes LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  SourceUhid (N bytes, UTF-8)                  |
|                          ...                                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|               DestinationUhid Length (4 bytes LE)             |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|               DestinationUhid (N bytes, UTF-8)                |
|                          ...                                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                    Payload Length (4 bytes LE)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                    Payload (N bytes)                           |
|                          ...                                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                   Signature Length (4 bytes LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                   Signature (N bytes, Ed25519)                |
|                          ...                                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                        Id (16 bytes, UUID)                    |
|                                                               |
|                                                               |
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

The minimum packet size with empty UHIDs, empty payload, and no signature is 50 bytes: `8 (nonce) + 8 (timestamp) + 1 (version) + 1 (type) + 1 (ttl) + 1 (priority) + 4 (source len) + 4 (dest len) + 4 (payload len) + 4 (sig len) + 16 (id) = 52 bytes`.

### 2.3. Signable Data Construction

The signature covers a deterministic byte sequence constructed as follows:

```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, little-endian int64)
|| Type (4 bytes, little-endian int32)
|| SourceUhidLength (4 bytes, little-endian int32)
|| SourceUhid (UTF-8 bytes)
|| DestinationUhidLength (4 bytes, little-endian int32)
|| DestinationUhid (UTF-8 bytes)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, little-endian int32)
|| Priority (4 bytes, little-endian int32)
```

Note: The payload itself is NOT included in the signed data; its SHA-256 hash is used instead. This allows intermediary nodes to verify packet integrity without decrypting the payload.

### 2.4. Packet Types

| Value | Name              | Direction     | Description |
|-------|-------------------|---------------|-------------|
| 1     | RouteRequest      | Broadcast     | AODV Route Request |
| 2     | RouteReply        | Unicast       | AODV Route Reply (MUST be signed by destination) |
| 3     | Data              | Unicast       | Application data |
| 4     | Ack               | Unicast       | Delivery acknowledgment |
| 5     | SosBroadcast      | Flood         | Emergency broadcast (see Section 8) |
| 6     | SosAck            | Unicast       | SOS acknowledgment |
| 7     | ChannelMessage    | Multicast     | Group channel message |
| 8     | ChunkRequest      | Unicast       | P2P content chunk request |
| 9     | ChunkData         | Unicast       | P2P content chunk response |
| 10    | Heartbeat         | Broadcast     | Periodic liveness signal |
| 11    | StreamAnnounce    | Broadcast     | Live stream advertisement |
| 12    | StreamSegment     | Unicast/Tree  | Live stream media segment |
| 13    | StreamSubscribe   | Unicast       | Request to join stream relay tree |
| 14    | StreamUnsubscribe | Unicast       | Leave stream relay tree |
| 15    | VoicePtt          | Unicast       | Push-to-talk voice frame |
| 16    | VoiceCall         | Unicast       | Real-time voice call frame |
| 17    | VoiceSignaling    | Unicast       | Voice call setup/teardown |
| 18    | DtnBundle         | Unicast       | DTN store-and-forward bundle (see Section 9) |
| 19    | DtnCustodyAck     | Unicast       | DTN custody transfer acknowledgment |
| 20    | DtnDeliveryReceipt| Unicast       | DTN end-to-end delivery confirmation |
| 21    | PresenceBeacon    | Broadcast     | Presence and availability announcement |
| 22    | PresenceQuery     | Unicast       | Presence status request |
| 23    | ProfileSync       | Unicast       | Profile metadata synchronization |
| 24    | TipPacket         | Unicast       | Node tipping (settled via LedgerAPI) |
| 25    | PreKeyRequest     | Unicast       | Request peer's pre-key bundle |
| 26    | PreKeyResponse    | Unicast       | Pre-key bundle delivery |

### 2.5. Node Capabilities

Nodes advertise their capabilities as a bitfield:

| Bit | Value | Capability  | Description |
|-----|-------|-------------|-------------|
| 0   | 1     | Ble         | Bluetooth Low Energy transport available |
| 1   | 2     | WifiDirect  | Wi-Fi Direct transport available |
| 2   | 4     | Gateway     | Internet gateway (bridges mesh to IP network) |
| 3   | 8     | Relay       | Willing to relay packets for others |
| 4   | 16    | Sos         | SOS broadcast capable |
| 5   | 32    | Streaming   | Live streaming relay capable |
| 6   | 64    | Voice       | Voice call relay capable |
| 7   | 128   | DtnCarrier  | DTN store-and-forward carrier |

---

## 3. Routing Algorithm

Aether uses a reactive routing protocol based on Ad-hoc On-demand Distance Vector (AODV) routing, extended with cryptographic route authentication and QoS-weighted route selection.

### 3.1. Route Request (RREQ)

When a node needs to send a packet to a destination for which it has no route, it initiates a Route Request:

1. The originator creates a `MeshPacket` with `Type = RouteRequest`, sets `SourceUhid` to itself, `DestinationUhid` to the target, and `TTL = 7` (the default).
2. The packet is broadcast to all directly connected peers.
3. Each intermediate node that receives an RREQ:
   a. Checks if it has already seen this RREQ by packet `Id`. If so, it silently drops the packet (deduplication). The deduplication cache holds up to 1,000 entries and is periodically flushed.
   b. Installs a **reverse route** to the RREQ originator. The reverse route records the UHID of the peer from which the RREQ was received as the next hop. Hop count is derived from `DefaultTtl - packet.Ttl + 1`.
   c. If it IS the destination, it generates an RREP (see Section 3.2).
   d. If it has an existing valid route to the destination, it MAY generate an RREP on behalf of the destination.
   e. Otherwise, it decrements TTL and re-broadcasts the RREQ.
4. The originator waits for an RREP with a timeout of **5,000 ms** (`RouteTimeoutMs`). If no RREP arrives, route discovery fails.

### 3.2. Route Reply (RREP)

When the destination (or an intermediate node with a valid route) generates a Route Reply:

1. A `MeshPacket` with `Type = RouteReply` is created, with `SourceUhid` set to the destination node and `DestinationUhid` set to the RREQ originator.
2. **SECURITY REQUIREMENT:** The RREP MUST be signed by the destination node's Ed25519 identity key. The signature covers the standard signable data (Section 2.3). This prevents route poisoning by malicious intermediate nodes.
3. The RREP is unicast back along the reverse route installed during RREQ propagation.
4. Each intermediate node that forwards the RREP:
   a. Verifies the RREP signature against the claimed source's public key (if known). If verification fails, the RREP is dropped and a warning is logged.
   b. Installs a **forward route** to the RREP source (the destination node) with the sender of the RREP as the next hop.
   c. Decrements TTL and forwards toward the RREQ originator.
5. When the RREP reaches the originator, the pending route request (tracked via `TaskCompletionSource`) is resolved with the installed route.

### 3.3. Route Maintenance

- **TTL-based expiry:** Every route entry carries an `ExpiresAt` timestamp set to `now + 300 seconds` (`RouteExpirySeconds`). Routes are not refreshed implicitly; they must be re-established via a new RREQ/RREP cycle after expiry.
- **Periodic pruning:** The protocol service runs a periodic heartbeat (default every 300 seconds). During each cycle, it removes expired routes from both the in-memory `ConcurrentDictionary` and the SQLite backing store.
- **RREQ dedup pruning:** The set of seen RREQ IDs is cleared when it exceeds 1,000 entries.

### 3.4. Route Quality and QoS

Each `RouteEntry` carries a `QualityScore` in the range [0, 100], initialized to 50 for newly discovered routes. The score considers:

- **Hop count:** Fewer hops generally indicates a faster route.
- **Latency:** Measured round-trip time when available.
- **Peer reliability:** The next-hop peer's reliability score (see Section 3.5).

Nodes that participate in the tipping incentive system receive a QoS boost to their route quality score. This is a soft preference: non-tippers always receive service, but consistent tippers may experience marginally better route selection. The boost tiers are:

| Tier    | Consistency Threshold | QoS Boost |
|---------|-----------------------|-----------|
| Bronze  | 25                    | +5        |
| Silver  | 50                    | +10       |
| Gold    | 75                    | +20       |

### 3.5. Peer Reliability Scoring

Every known peer is assigned a reliability score in the range [0, 100], initialized to 50 (`DefaultReliabilityScore`). The score is adjusted based on observed behavior:

| Event                | Delta |
|----------------------|-------|
| Successful relay     | +2    |
| Failed relay         | -5    |
| SOS relay            | +5    |
| Chunk served         | +1    |
| Chunk serve failure  | -10   |

Reliability scores are persisted to SQLite and loaded into memory at startup. The score influences route selection: routes through more reliable peers are preferred.

---

## 4. Key Exchange

Aether implements a key exchange mechanism derived from the Extended Triple Diffie-Hellman (X3DH) protocol, combined with a symmetric ratchet for forward secrecy. A full Diffie-Hellman ratchet (Double Ratchet) is specified for future implementation when always-on transports (BLE GATT) are available.

### 4.1. Identity Keys

Each node generates an **Ed25519** identity key pair at first launch:

- **Private key:** 32-byte seed, stored in platform secure storage (MAUI SecureStorage on mobile, OS keychain on desktop).
- **Public key:** 32-byte Ed25519 public key, published to the network and AetherAPI.
- **Migration:** Nodes that were initialized with ECDSA P-256 identity keys (Protocol Version 1) are supported during a 30-day fallback window. Signature verification attempts Ed25519 first; if the public key is longer than 32 bytes, it falls back to P-256 ECDSA verification.

Ed25519 is used for:
- Packet signing (Section 2.3)
- Pre-key bundle signing (Section 4.3)
- RREP authentication (Section 3.2)
- Tip signature verification

### 4.2. Ephemeral Keys

Key agreement uses **ECDH with the NIST P-256 curve**. P-256 is chosen for native .NET runtime support (`ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)`) without external library dependencies.

### 4.3. Pre-Key Bundle

A pre-key bundle is published to allow asynchronous session establishment (the recipient need not be online when the sender initiates):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Ed25519 public key
    PreKeyId:               int32       // Cryptographically random ID
    PreKey:                 byte[]      // ECDH P-256 public key (DER SubjectPublicKeyInfo)
    SignedPreKeyId:          int32       // Cryptographically random ID
    SignedPreKey:            byte[]      // ECDH P-256 public key (DER SubjectPublicKeyInfo)
    SignedPreKeySignature:   byte[]      // Ed25519 signature over SignedPreKey bytes
}
```

Pre-key IDs are generated using `RandomNumberGenerator.GetInt32(1, int.MaxValue)` to prevent prediction.

### 4.4. Session Establishment (X3DH Variant)

When Alice wants to establish an encrypted session with Bob:

1. Alice obtains Bob's `PreKeyBundle` (via the network or AetherAPI).
2. Alice verifies `SignedPreKeySignature` using Bob's `IdentityKey` (Ed25519, with P-256 fallback). If verification fails, the bundle is rejected.
3. Alice generates an ephemeral ECDH P-256 key pair.
4. Alice performs ECDH key agreement between her ephemeral private key and Bob's `SignedPreKey` public key, producing a 32-byte `sharedSecret`.
5. Alice derives three keys using HKDF-SHA256:

   ```
   Salt:           "AetherSignal" (UTF-8, 12 bytes)

   RootKey       = HKDF-SHA256(sharedSecret, salt, info="aether-root-v1",     outputLength=32)
   SendChainKey  = HKDF-SHA256(sharedSecret, salt, info="aether-chain-send-v1", outputLength=32)
   RecvChainKey  = HKDF-SHA256(sharedSecret, salt, info="aether-chain-recv-v1", outputLength=32)
   ```

6. The `sharedSecret` is immediately zeroed using `CryptographicOperations.ZeroMemory`.
7. A `SignalSession` is created and persisted to SQLite:

   ```
   SignalSession {
       RootKey:            byte[32]
       SendChainKey:       byte[32]
       RecvChainKey:       byte[32]
       SendCounter:        int32       // Initialized to 0
       RecvCounter:        int32       // Initialized to 0
       LocalRatchetKey:    byte[]      // Alice's ephemeral ECDH private key
       RemoteRatchetKey:   byte[]      // Bob's signed pre-key (DER)
       PreKeyUsed:         int32       // Bob's PreKeyId consumed
       SkippedMessageKeys: map<int, byte[32]>  // For out-of-order decryption
       CreatedAt:          timestamp
       UpdatedAt:          timestamp
   }
   ```

8. After session creation, intermediate key material (RootKey, SendChainKey, RecvChainKey) is zeroed from the establishment context. The session object retains its own copies.

**Important:** If no Signal session exists for a recipient, the message is NOT sent insecurely. It is queued in the outbox, and a `SessionRequired` event is raised to trigger pre-key bundle exchange. There is no UHID-derived fallback encryption.

### 4.5. Symmetric Ratchet

Once a session is established, each message is encrypted with a unique key derived from the chain:

**Sending:**

1. Derive `messageKey = HMAC-SHA256(SendChainKey, counter_bytes)` where `counter_bytes` is the 4-byte little-endian representation of `SendCounter`.
2. Advance the chain: `SendChainKey = HMAC-SHA256(SendChainKey, 0x01)`.
3. Increment `SendCounter`.
4. Encrypt the plaintext using AES-256-GCM with the `messageKey`:
   - Nonce: 12 bytes, cryptographically random.
   - Tag: 16 bytes, appended to ciphertext.
   - Ciphertext format: `[encrypted_data || 16-byte_tag]`.
5. Zero the `messageKey` immediately after encryption.

**Receiving:**

1. If the incoming `Counter` matches a key in `SkippedMessageKeys`, use that key, decrypt, remove from map, and return.
2. If `Counter < RecvCounter`, reject as duplicate/expired.
3. If `Counter > RecvCounter` and the gap exceeds `MaxSkippedKeys` (1,000), reject the message and invalidate the session. The sender must re-establish via a new pre-key exchange. This prevents memory exhaustion attacks.
4. If `Counter > RecvCounter` within the allowed gap, derive and store skipped keys for each counter value from `RecvCounter` to `Counter - 1`.
5. Derive the message key for the current counter, decrypt, advance `RecvChainKey`, increment `RecvCounter`.
6. Zero the `messageKey` after decryption.

### 4.6. Encrypted Payload Format

```
EncryptedPayload {
    Ciphertext:     byte[]      // AES-256-GCM ciphertext + 16-byte tag
    Nonce:          byte[12]    // AES-GCM nonce
    MessageType:    int32       // 1 = PreKey message, 2 = Regular
    SenderUhid:     string      // Sender's UHID
    Counter:        int32       // Message sequence number within session
    EncryptedAt:    timestamp
}
```

---

## 5. Transport Layer Requirements

Aether is transport-agnostic. Any physical communication channel that satisfies the `ITransportService` contract can participate in the mesh.

### 5.1. ITransportService Interface Contract

Every transport implementation MUST expose the following:

**Properties:**

| Property           | Type   | Description |
|--------------------|--------|-------------|
| `Name`             | string | Human-readable identifier (e.g., "BLE", "Wi-Fi Direct", "NearLink") |
| `IsAvailable`      | bool   | Whether the transport is currently usable on this device |
| `MaxBandwidthBps`  | int64  | Maximum throughput in bytes per second |
| `MaxRangeMeters`   | int32  | Maximum communication range in meters |
| `PowerCostRelative`| int32  | Relative power consumption (1 = low, 10 = high) |
| `MaxConcurrentPeers` | int32 | Maximum simultaneous peer connections |

**Methods:**

| Method         | Signature | Description |
|----------------|-----------|-------------|
| `SendAsync`    | `Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken)` | Send a byte array to a specific peer. Returns true on success. |
| `SendStreamAsync` | `Task<bool> SendStreamAsync(string peerUhid, Stream data, CancellationToken)` | Send a stream to a peer (for large transfers, voice, video). |
| `IsConnected`  | `bool IsConnected(string peerUhid)` | Check if a connection is active to a peer. |

**Events:**

| Event          | Signature | Description |
|----------------|-----------|-------------|
| `DataReceived` | `EventHandler<(string SenderUhid, byte[] Data)>` | Fired when data arrives from a peer. |

### 5.2. Transport Selection Algorithm

The `TransportManager` selects the optimal transport for each packet based on:

1. **Availability:** Only transports where `IsAvailable == true` are considered.
2. **Payload size:** If payload size is at or below `BleMaxPayloadBytes` (1,024 bytes), BLE is preferred for power efficiency. Larger payloads prefer Wi-Fi Direct.
3. **Power cost weighting:** Among available transports, lower `PowerCostRelative` values are preferred for routine traffic. High-priority packets (SOS, voice) may override this preference.
4. **Peer connectivity:** If a transport already has an active connection to the target peer (`IsConnected` returns true), it is preferred to avoid connection setup overhead.
5. **Fallback:** If no local transport can reach the target, the packet is queued for server relay via AetherAPI.

### 5.3. Reference Transports

| Transport    | MaxBandwidth   | MaxRange | PowerCost | MaxPeers | Notes |
|-------------|----------------|----------|-----------|----------|-------|
| BLE 5.0     | ~2 Mbps        | 100m     | 1         | 7        | Primary discovery + small packets |
| Wi-Fi Direct| ~250 Mbps      | 200m     | 5         | 8        | Large transfers, streaming, voice |
| NearLink    | ~900 Mbps      | 200m     | 3         | 16       | Huawei/HiSilicon, high throughput |

**BLE payload limit:** Packets exceeding 1,024 bytes (`BleMaxPayloadBytes`) are automatically routed to Wi-Fi Direct or NearLink. BLE is used for discovery advertisements, small control packets (RREQ/RREP, presence beacons), and low-bandwidth messaging.

**Wi-Fi Direct** connection timeout is 10,000 ms (`WifiDirectTimeoutMs`) with a maximum of 8 concurrent peers (`MaxWifiDirectPeers`).

---

## 6. Discovery Protocol

### 6.1. BLE Advertising

Aether nodes discover each other primarily through BLE advertising. To prevent persistent tracking via static identifiers, the protocol employs two privacy mechanisms: rotating Service UUIDs and Identity Resolving Keys.

**Advertising cycle:** 2 seconds scanning on, 8 seconds off (`BleScanOnMs`/`BleScanOffMs`). The advertise interval is 1,000 ms (`BleAdvertiseIntervalMs`). A random jitter of 0-2,000 ms (`BleScanJitterMaxMs`) is added to the scan interval to prevent timing pattern detection.

**Peer timeout:** A peer not re-discovered within 30 seconds is considered lost (`PeerLost` event).

### 6.2. Rotating Service UUID

To prevent long-term BLE fingerprinting, the Service UUID used in advertisements rotates every 15 minutes (`BleUuidRotationSeconds = 900`):

```
window     = floor(unix_timestamp_seconds / 900)
hmac       = HMAC-SHA256(rotation_key, little-endian-int64(window))
service_uuid = format_as_uuid(hmac[0..15])
```

The `rotation_key` is a 32-byte key generated once per node and stored in secure storage. All Aether nodes sharing the same rotation key will derive the same UUID for a given time window, enabling mutual discovery without revealing a permanent identifier.

A static fallback UUID (`A3E7-1001-0001-0000-000000000000`) is maintained for 90 days during transition from the non-rotating scheme.

### 6.3. Identity Resolving Key (IRK)

Each node generates a 128-bit Identity Resolving Key (IRK) stored in secure storage. The IRK is shared with trusted peers during key exchange.

**Resolvable Private Address (RPA) generation:**

1. Compute `prand = HMAC-SHA256(IRK, window_bytes)[0..2]` (3 bytes).
2. Set the two most significant bits of `prand[0]` to `01` (RPA flag per BLE spec).
3. Compute `hash = AES-128-ECB(IRK, pad(prand))` where `prand` occupies bytes 13-15 of a 16-byte zero-padded input.
4. Construct RPA: `hash[0..2] || prand[0..2]` (6 bytes total).

**RPA resolution:** A node that possesses a peer's IRK can verify whether an observed RPA belongs to that peer by recomputing the hash from the RPA's `prand` component. Resolution time is approximately O(N) where N is the number of known IRKs, benchmarked at ~0.1ms for 100 peers.

The RPA rotates on the same 15-minute cycle as the Service UUID.

### 6.4. Geohash-Based Proximity

Nodes optionally encode their location as a geohash. For privacy, the geohash is truncated to 4 characters, providing a resolution of approximately 39km x 20km. This granularity is sufficient for:

- Proximity-based channel discovery
- DTN epidemic routing (replicate toward the recipient's last known geohash area)
- SOS alert geographic context

The full-precision geohash is never transmitted over the mesh. Only the truncated form is shared, and only when the node's privacy level permits (`PrivacyLevel.Full` or `PrivacyLevel.Partial`).

---

## 7. Security Model

### 7.1. Threat Model

Aether assumes the following adversary capabilities:

- **Passive eavesdropping:** The adversary can observe all BLE advertisements and mesh traffic within radio range.
- **Active injection:** The adversary can inject, modify, or replay packets.
- **Sybil attack:** The adversary can create multiple fake node identities.
- **Selective denial of service:** The adversary can selectively drop packets as a relay node.

### 7.2. What Is Protected

| Property | Protection Level | Mechanism |
|----------|-----------------|-----------|
| Message content | Full confidentiality | AES-256-GCM with per-message keys (Section 4.5) |
| Sender identity | Partial | UHID visible in packet headers; BLE address rotates (Section 6) |
| Receiver identity | Partial | Destination UHID visible in routed packets; broadcast packets have empty destination |
| Routing metadata | Minimal | Intermediate nodes see source/destination UHIDs and TTL |
| Message ordering | Protected | Counters in symmetric ratchet prevent reordering |
| Message integrity | Full | Ed25519 signature on every packet (v2) |

### 7.3. Attack Resistance

**Replay attacks:**
Each packet carries an 8-byte cryptographically random nonce and a millisecond-precision timestamp. Relay nodes maintain a deduplication cache of `(SenderUhid, NonceValue)` pairs with a 5-minute TTL (`MaxPacketAgeSeconds = 300`). A packet with a duplicate nonce from the same sender is dropped. Packets with timestamps older than 5 minutes are rejected regardless of nonce.

The nonce dedup cache is cleaned every 60 seconds. Expired entries (older than 5 minutes) are removed.

**Man-in-the-middle (MITM):**
- Route Reply packets MUST carry a valid Ed25519 signature from the claimed destination node. Intermediate nodes cannot forge RREPs because they do not possess the destination's private key.
- Pre-key bundles include a `SignedPreKeySignature` (Ed25519) over the `SignedPreKey`, binding the ephemeral ECDH key to the long-term identity.
- Session establishment (Section 4.4) cryptographically binds the session to both parties' identities through the pre-key verification step.

**Sybil attacks:**
- Each node's reliability score starts at 50 and is adjusted based on observed behavior (Section 3.5). Newly created Sybil nodes have no accumulated reputation.
- Nodes with low reliability scores (approaching 0) are deprioritized in route selection.
- The DTN epidemic routing algorithm uses geohash proximity and relay success history to select replication targets, making it harder for Sybil nodes to attract traffic without genuine relay contributions.

**Flooding attacks:**
- TTL is decremented at each hop and packets with TTL = 0 are dropped. The default TTL of 7 limits the blast radius of any broadcast.
- RREQ deduplication by packet ID prevents amplification through broadcast storms. The dedup cache is flushed at 1,000 entries.
- SOS broadcasts are rate-limited to 3 per hour per node (Section 8).

### 7.4. Key Zeroing

All intermediate cryptographic material is zeroed immediately after use:

- `sharedSecret` from ECDH key agreement: zeroed after HKDF derivation.
- `messageKey` from chain ratchet: zeroed after AES-GCM encrypt/decrypt.
- `skippedKey` from out-of-order decryption: zeroed after use and removed from the map.
- Derived `RootKey`, `SendChainKey`, `RecvChainKey`: zeroed from the establishment context (the session retains its own copies).

Zeroing uses `CryptographicOperations.ZeroMemory` which is guaranteed not to be optimized away by the compiler.

### 7.5. P-256 to Ed25519 Migration

The protocol supports a 30-day transition window from ECDSA P-256 identity keys (Protocol Version 1) to Ed25519 (Protocol Version 2):

1. Protocol Version 1 packets (unsigned) are accepted during the transition period.
2. Signature verification first attempts Ed25519. If the public key is longer than 32 bytes (indicating a DER-encoded P-256 key), it falls back to P-256 ECDSA verification.
3. After the 30-day window, Protocol Version 1 packets are rejected.
4. Nodes that have not migrated must re-initialize with a new Ed25519 identity.

### 7.6. Jurisdiction Awareness

The protocol defines jurisdiction tiers to handle varying legal requirements around encryption and mesh networking:

| Tier | Behavior | Example Jurisdictions |
|------|----------|-----------------------|
| 1    | Operate freely | South Africa, Kenya, Ghana |
| 2    | Modified operation | Nigeria, India, EU, US, UK |
| 3    | Mesh-only (high risk) | China, Russia, Iran, UAE, Myanmar |
| 4    | Unknown (default mesh-only) | All others |

Tier selection affects feature availability (e.g., tipping/financial features may be disabled in Tier 3) but does not weaken encryption. End-to-end encryption is always applied regardless of jurisdiction.

---

## 8. SOS Broadcast

The SOS mechanism is a dual-path emergency flood designed for situations where a user is in danger and needs to reach nearby mesh peers and/or the internet simultaneously.

### 8.1. Broadcast Parameters

| Parameter | Value | Description |
|-----------|-------|-------------|
| TTL       | 15    | Twice the normal default (7), ensuring wider propagation |
| Priority  | 999   | Maximum priority; preempts all other traffic in relay queues |
| Rate limit| 3/hour| Per-node limit to prevent abuse |
| Destination| empty | Broadcast to all peers (no specific destination) |

### 8.2. Flood Algorithm

1. The originator constructs an SOS packet with `Type = SosBroadcast`, `TTL = 15`, `Priority = 999`, and an empty `DestinationUhid`.
2. The payload is JSON-encoded and contains:
   ```json
   {
       "broadcast_id": "UUID",
       "broadcast_type": "sos",
       "message": "optional text",
       "latitude": -33.9249,
       "longitude": 18.4241,
       "geohash": "k3vn"
   }
   ```
3. **Dual-path dispatch:** The SOS is sent simultaneously via:
   - **Mesh flood:** Broadcast to all connected peers via all available transports.
   - **API call:** Sent to AetherAPI for server-side distribution and bridging to PanikAPI (SMS/email dispatch).
4. Both paths are fire-and-forget relative to each other. If the API call fails, the mesh flood proceeds independently.

### 8.3. Relay Behavior

When a node receives an SOS packet:

1. Check deduplication by packet `Id`. If already seen, drop silently.
2. Deserialize the payload and raise the `SosReceived` event for the local UI.
3. Add the alert to the active alerts list.
4. If `TTL > 1`, decrement TTL and **re-broadcast to ALL peers** regardless of routing table state. SOS packets bypass normal routing -- they flood unconditionally.

### 8.4. Rate Limiting

Each node maintains a sliding window of recent broadcast timestamps. Before initiating a new SOS:

1. Prune entries older than 1 hour from the queue.
2. If the queue contains 3 or more entries (`MaxSosBroadcastsPerHour`), the broadcast is rejected.
3. On successful dispatch, the current timestamp is enqueued.

Rate limiting applies only to originating SOS broadcasts, not to relaying.

### 8.5. SOS-PanikAPI Bridge

SOS broadcasts received via the mesh can be forwarded to PanikAPI for traditional emergency response (SMS to contacts, email alerts). Conversely, PanikAPI emergency sessions can be broadcast to the mesh for community awareness. Loop prevention is achieved by marking the source (`direct` vs `mesh_forward`) and an `internet_forwarded` flag on mesh broadcasts.

---

## 9. DTN Store-and-Forward

The Delay-Tolerant Networking (DTN) subsystem enables message delivery when no end-to-end path exists between sender and recipient. Bundles are stored on intermediate nodes and forwarded opportunistically as connectivity changes.

### 9.1. Bundle Format

```
DtnBundle {
    Id:                 UUID        // Unique bundle identifier
    SenderUhid:         string      // Originator's UHID
    RecipientUhid:      string      // Intended recipient's UHID
    EncryptedPayload:   byte[]      // End-to-end encrypted content
    Priority:           enum        // Low(0), Normal(1), High(2), Sos(3)
    Status:             enum        // Pending(0), InCustody(1), Delivered(2), Expired(3), Failed(4)
    CopyCount:          int32       // Current number of copies in the network (initialized to 1)
    MaxCopies:          int32       // Maximum allowed copies (default: 3)
    SenderGeohash:      string?     // Truncated geohash of sender at creation time
    RecipientLastGeohash: string?   // Last known geohash of recipient (for proximity routing)
    HopCount:           int32       // Number of custody transfers completed
    CreatedAt:          timestamp
    ExpiresAt:          timestamp   // Default: CreatedAt + 72 hours
}
```

### 9.2. Bundle Lifecycle

1. **Creation:** The sender creates a bundle with an encrypted payload (encrypted via the Signal session with the recipient). `Status = Pending`, `CopyCount = 1`.
2. **Immediate delivery attempt:** The sender first attempts direct mesh routing (RREQ/RREP). If a route exists, the bundle is delivered immediately and `Status` transitions to `Delivered`.
3. **Server relay attempt:** If mesh routing fails, the sender attempts to relay through AetherAPI. If the server can reach the recipient (or queue the message), delivery succeeds.
4. **Store-and-forward:** If both mesh and server relay fail, the bundle remains in local storage (`Pending` status) awaiting the next delivery scan.

### 9.3. Delivery Scan

A periodic scan runs every 60 seconds (`DtnScanIntervalSeconds`):

1. Load all pending bundles from SQLite (source of truth).
2. For each pending bundle:
   a. Attempt mesh route to recipient.
   b. Attempt server relay.
   c. If both fail and `CopyCount < MaxCopies`, attempt epidemic replication (Section 9.4).
3. Remove expired bundles (`ExpiresAt <= now`).

### 9.4. Epidemic Routing

When direct delivery and server relay both fail, bundles are replicated to nearby peers using epidemic routing:

1. The `EpidemicRoutingService` selects replication targets from the current peer list.
2. Target selection considers:
   - **Geohash proximity:** Peers whose geohash is closer to the recipient's last known geohash are preferred.
   - **Relay history:** Peers with higher reliability scores are preferred.
   - **Copy budget:** Replication stops when `CopyCount >= MaxCopies` (default: 3).
3. Each replication sends a `DtnBundle` packet to the selected peer.
4. Upon receipt, the peer's DTN service invokes `AcceptCustodyAsync`.

### 9.5. Custody Transfer

When a node receives a DTN bundle intended for another node:

1. **Capacity check:** The node checks its current bundle count against `DtnMaxBundlesPerNode` (50). If at capacity, custody is rejected.
2. **Accept:** The bundle status is set to `InCustody`, hop count is incremented, and the bundle is persisted to SQLite.
3. **Custody record:** A `CustodyRecord` is created documenting the transfer (from, to, timestamp).
4. **Copy count increment:** The bundle's `CopyCount` is incremented in persistent storage.
5. **Acknowledgment:** A `DtnCustodyAck` packet is sent back to the transferring node with `Accepted = true`.
6. The accepting node becomes responsible for attempting delivery on subsequent scans.

### 9.6. Delivery Receipt

When the intended recipient receives a DTN bundle:

1. The bundle status is updated to `Delivered`.
2. A `DtnDeliveryReceipt` is sent back to the original sender via mesh routing (with server relay fallback):
   ```
   DtnDeliveryReceipt {
       BundleId:               UUID
       RecipientUhid:          string
       TotalHops:              int32
       TotalCustodyTransfers:  int32
       DeliveredAt:            timestamp
   }
   ```
3. Upon receiving the receipt, the sender removes the bundle from its store and fires the `BundleDelivered` event.
4. The receipt is also synced to AetherAPI for analytics.

### 9.7. Bundle Expiry

- Default bundle TTL is 72 hours (`DtnBundleTtlHours`).
- Expired bundles are cleaned up during the periodic delivery scan.
- Bundles in `Expired` or `Delivered` status are removed from both in-memory cache and SQLite.

### 9.8. Capacity Limits

| Parameter               | Default | Description |
|-------------------------|---------|-------------|
| `DtnBundleTtlHours`    | 72      | Maximum bundle lifetime |
| `DtnMaxCopies`          | 3       | Maximum copies per bundle across the network |
| `DtnMaxBundlesPerNode`  | 50      | Maximum bundles a single node will carry |
| `DtnScanIntervalSeconds`| 60      | Delivery scan frequency |

---

## Appendix A: Constants Reference

All protocol constants are defined in `ProtocolConstants` and are reproduced here for reference:

### Routing
| Constant              | Value  |
|-----------------------|--------|
| DefaultTtl            | 7      |
| SosTtl                | 15     |
| RouteTimeoutMs        | 5000   |
| RouteExpirySeconds    | 300    |

### BLE Discovery
| Constant                  | Value  |
|---------------------------|--------|
| BleDiscoveryIntervalMs    | 10000  |
| BleScanOnMs               | 2000   |
| BleScanOffMs              | 8000   |
| BleAdvertiseIntervalMs    | 1000   |
| BleUuidRotationSeconds    | 900    |
| BleScanJitterMaxMs        | 2000   |
| AetherBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

### Security
| Constant                  | Value  |
|---------------------------|--------|
| PacketNonceSize           | 8      |
| MaxPacketAgeSeconds       | 300    |
| ProtocolVersionUnsigned   | 1      |
| ProtocolVersionSigned     | 2      |
| MaxSkippedKeys            | 1000   |
| AES-GCM Nonce Size        | 12     |
| AES-GCM Tag Size          | 16     |

### SOS
| Constant                   | Value |
|----------------------------|-------|
| SosTtl                     | 15    |
| SosPriority                | 999   |
| MaxSosBroadcastsPerHour    | 3     |

### DTN
| Constant                  | Value  |
|---------------------------|--------|
| DtnBundleTtlHours         | 72     |
| DtnMaxCopies              | 3      |
| DtnMaxBundlesPerNode       | 50     |
| DtnScanIntervalSeconds     | 60     |

### Transport
| Constant                  | Value   |
|---------------------------|---------|
| BleMaxPayloadBytes        | 1024    |
| DefaultChunkSizeBytes     | 262144  |
| MaxChunkSizeBytes         | 1048576 |
| WifiDirectTimeoutMs       | 10000   |
| MaxWifiDirectPeers        | 8       |

### Heartbeat
| Constant                      | Value |
|-------------------------------|-------|
| HeartbeatIntervalSeconds      | 300   |
| NodeOfflineThresholdSeconds   | 900   |

### Presence
| Constant                          | Value |
|-----------------------------------|-------|
| PresenceBeaconIntervalMs          | 15000 |
| PresenceTimeoutSeconds            | 60    |
| EphemeralIdRotationMinutes        | 15    |
| ProximityEventDebounceSeconds     | 30    |

### Voice
| Constant                  | Value |
|---------------------------|-------|
| VoiceFrameDurationMs      | 20    |
| PttMaxDurationSeconds     | 60    |
| JitterBufferMinMs         | 20    |
| JitterBufferMaxMs         | 200   |
| OpusDefaultBitrateKbps    | 64    |
| MaxGroupVoiceMembers      | 8     |

### Streaming
| Constant                    | Value |
|-----------------------------|-------|
| DefaultSegmentDurationMs    | 3000  |
| MaxStreamTreeFanout         | 4     |
| MaxStreamRelayHops          | 3     |
| StreamSegmentBufferSize     | 10    |
| BleAudioBitrateKbps        | 64    |
| WifiDirectVideoBitrateKbps  | 500   |

---

## Appendix B: Glossary

| Term | Definition |
|------|------------|
| **UHID** | Universal Hardware Identifier. A unique string identifying a mesh node, derived from device identity and cryptographic keys. |
| **RREQ** | Route Request. A broadcast packet used to discover a path to a destination node. |
| **RREP** | Route Reply. A unicast packet sent back along the reverse route established by an RREQ. |
| **IRK** | Identity Resolving Key. A 128-bit key used to generate and resolve BLE Resolvable Private Addresses. |
| **RPA** | Resolvable Private Address. A 6-byte BLE address that rotates periodically but can be resolved by peers holding the sender's IRK. |
| **X3DH** | Extended Triple Diffie-Hellman. A key agreement protocol enabling asynchronous session establishment. |
| **DTN** | Delay-Tolerant Networking. A store-and-forward paradigm for environments with intermittent connectivity. |
| **Gateway** | A mesh node that has internet connectivity and bridges mesh traffic to/from IP-based services. |
| **HKDF** | HMAC-based Key Derivation Function. Used to derive multiple keys from a single shared secret. |
| **Pre-key bundle** | A published set of keys allowing a sender to establish an encrypted session without the recipient being online. |

---

## Appendix C: References

1. C. Perkins, E. Belding-Royer, S. Das, "Ad hoc On-Demand Distance Vector (AODV) Routing," RFC 3561, July 2003.
2. M. Marlinspike, T. Perrin, "The X3DH Key Agreement Protocol," Signal Foundation, November 2016.
3. T. Perrin, M. Marlinspike, "The Double Ratchet Algorithm," Signal Foundation, November 2016.
4. H. Krawczyk, P. Eronen, "HMAC-based Extract-and-Expand Key Derivation Function (HKDF)," RFC 5869, May 2010.
5. K. Fall, "A Delay-Tolerant Network Architecture for Challenged Internets," SIGCOMM 2003.
6. Bluetooth SIG, "Bluetooth Core Specification v5.0," December 2016 (Resolvable Private Address, Section 1.3.2.2).
7. NIST, "Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM)," SP 800-38D, November 2007.
8. D. J. Bernstein et al., "High-speed high-security signatures," Journal of Cryptographic Engineering, 2012 (Ed25519).
