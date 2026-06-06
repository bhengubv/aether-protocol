# Aether Mesh Networking Protocol Specification

**Version:** 2.0
**Status:** Reconciled with HEAD (2026-05-05)
**Date:** 2026-03-15 (initial draft); 2026-05-05 (§2, §4, §10, §11 reconciled, §3/§9 verified)
**Authors:** The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V.

> **Reader notice.** Earlier drafts of this document predate the
> 8-language wire-format alignment and the family-wide port to X25519 +
> Signal Double Ratchet. As of 2026-05-05, §2 (Packet Format), §3
> (Routing), §4 (Key Exchange), §9 (DTN) describe the implemented
> protocol; §10 (Video Streaming) and §11 (Watch Together) describe the
> target protocol — they are wire-defined and fixture-tested but the
> codec / BitTorrent / ChipIn pipelines are not yet bound to the
> scaffolding. The C# reference is authoritative everywhere this
> document and the implementation diverge.
>
> - Canonical wire bytes: `fixtures/expected/*.bin` (10 named cases)
> - Reference serializer: `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> - Reference Signal stack: `src/AetherNet.Security/Services/SignalProtocolService.cs`
> - Reference routing: `src/AetherNet.Core/Routing/RoutingService.cs`
> - Reference DTN: `src/AetherNet.Core/Dtn/DtnService.cs`
> - Cross-language wire interop proof: `fixtures/README.md`
> - Cross-language Signal interop proof: `fixtures/signal/README.md`

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
10. [Video Streaming](#10-video-streaming)
11. [Watch Together](#11-watch-together)

---

## 1. Abstract

Aether is a decentralised mesh networking protocol designed for environments with intermittent or absent internet connectivity. It provides multi-hop packet routing over heterogeneous short-range transports (Bluetooth Low Energy, Wi-Fi Direct, NearLink), end-to-end encryption using an X3DH-derived key agreement with a symmetric ratchet, delay-tolerant store-and-forward delivery, and an emergency SOS flood mechanism. The protocol is transport-agnostic: any physical layer that can send and receive byte arrays between peers is a valid Aether transport. Nodes are identified by Universal Hardware Identifiers (UHIDs) and authenticated via Ed25519 identity keys. Aether is intended as a universal network layer -- every application in the ecosystem registers Aether services, and nodes without internet connectivity reach the wider network through gateway peers that bridge mesh traffic to the internet.

---

## 2. Packet Format

> Reconciled 2026-05-05 against `src/AetherNet.Core/Protocol/PacketSerializer.cs`
> and the 10 fixture cases under `fixtures/expected/`.

### 2.1. MeshPacket Wire Layout

Every Aether message is encapsulated in a `MeshPacket`. Fields appear on the
wire in **exactly** this order:

| Off | Field            | Type                            | Size       | Notes |
|-----|------------------|---------------------------------|------------|-------|
| 0   | ProtocolVersion  | uint8                           | 1          | `1` = unsigned (legacy), `2` = signed (current) |
| 1   | Type             | uint8                           | 1          | Packet type enumeration (see §2.4) |
| 2   | Id               | UUID, RFC 4122 big-endian       | 16         | Packet identifier for deduplication. **Big-endian** byte order, NOT .NET's mixed-endian Guid default. |
| 18  | Priority         | uint8                           | 1          | Priority level (0 = normal, 255 = SOS). **Wire field is 1 byte; values >255 must be clamped.** |
| 19  | Ttl              | int32, little-endian            | 4          | Time-to-live, decremented at each hop. **4-byte int32**, NOT 1-byte uint8 — values up to ~2³¹-1 are valid. |
| 23  | TimestampMs      | int64, little-endian            | 8          | Unix epoch milliseconds (UTC). |
| 31  | SourceUhid Len   | uint16, little-endian           | 2          | Length of `SourceUhid` in UTF-8 bytes. Max 65535. |
| 33  | SourceUhid       | UTF-8 bytes                     | N          | Sender's UHID; empty allowed but unusual. |
| 33+N | DestinationUhid Len | uint16, little-endian        | 2          | Length of `DestinationUhid` in UTF-8 bytes. |
| ... | DestinationUhid  | UTF-8 bytes                     | M          | Recipient's UHID; empty string for broadcast. |
| ... | PacketNonce Len  | uint16, little-endian           | 2          | Length of `PacketNonce` in bytes. Standard value: 8. |
| ... | PacketNonce      | bytes                           | P          | Cryptographically random nonce for replay prevention. |
| ... | Payload Len      | int32, little-endian            | 4          | Length of `Payload` in bytes. Negative values are an error. |
| ... | Payload          | bytes                           | Q          | Application data. Interpretation depends on `Type`. |
| ... | Signature Len    | uint16, little-endian           | 2          | Length of `Signature` in bytes. 0 (unsigned) or 64 (Ed25519). |
| ... | Signature        | bytes                           | R          | Ed25519 signature over signable data (see §2.3). |

**Length-prefix widths** vary by field — `SourceUhid`, `DestinationUhid`,
`PacketNonce`, and `Signature` use **2-byte (uint16)** length prefixes;
`Payload` uses a **4-byte (int32)** length prefix because payloads can exceed
64 KiB.

### 2.2. Minimum Packet Size

With every variable-length field empty (zero-length UHIDs, zero-length nonce,
zero-length payload, zero-length signature), the wire size is:

```
1 (version) + 1 (type) + 16 (id) + 1 (priority) + 4 (ttl)
  + 8 (timestamp) + 2 (src len) + 2 (dst len)
  + 2 (nonce len) + 4 (payload len) + 2 (sig len)
= 43 bytes
```

The 50-byte / 52-byte figures in earlier drafts of this spec were incorrect.

### 2.3. Wire Format Diagram

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| ProtoVer | Type    |              Id (bytes 0..3)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Id (bytes 4..15, RFC 4122 BE)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Priority |                  Ttl (4 bytes int32 LE)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  TimestampMs (8 bytes int64 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                                  ...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  SourceUhid Len (uint16 LE)  |        SourceUhid (UTF-8)       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  DestUhid Len (uint16 LE)    |        DestUhid (UTF-8)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Nonce Len (uint16 LE)       |        Nonce (bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|              Payload Len (int32 LE)                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Payload (bytes)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Signature Len (uint16 LE)   |        Signature (bytes)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

For a worked example, see `fixtures/expected/basic_data.bin` (83 bytes,
canonical input in `fixtures/inputs.json`). Implementations are validated
against the full fixture corpus — any divergence fails the cross-language
fixture-verifier test.

### 2.4. Signable Data Construction

The signature (`Signature` field on the wire) is computed over a separate
canonical byte sequence — **not** over the wire bytes themselves. This
allows the wire layout to evolve without breaking signatures, and lets
intermediary nodes verify integrity without seeing the plaintext payload
(only its SHA-256 hash is signed).

The signable byte sequence is the concatenation:

```
PacketNonce (8 bytes)
|| TimestampMs            (8 bytes, little-endian int64)
|| Type                   (4 bytes, little-endian int32)
|| SourceUhidLength       (4 bytes, little-endian int32)
|| SourceUhid             (UTF-8 bytes)
|| DestinationUhidLength  (4 bytes, little-endian int32)
|| DestinationUhid        (UTF-8 bytes)
|| SHA-256(Payload)       (32 bytes)
|| Ttl                    (4 bytes, little-endian int32)
|| Priority               (4 bytes, little-endian int32, clamped to [0,255])
```

> Note the deliberate divergence from the wire layout in §2.1: the signable
> data uses **4-byte int32** for `Type`, `Length`, `Ttl`, and `Priority`,
> while the wire uses 1-byte / 2-byte / 4-byte / 1-byte respectively.
> This is intentional — the signable form is portable across languages and
> uses fixed-width fields; the wire form is compact for BLE PDU economy.
> Implementations must clamp `Priority` to `[0,255]` before encoding into
> signable bytes, otherwise the receiver (which sees the wire byte 0..255)
> derives a different signable buffer and verification fails.

The reference implementation lives at `src/AetherNet.Security/Services/
PacketSigningService.cs::BuildSignableData` and is required reading for
porting.

### 2.5. Packet Types

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
| 27    | VideoCall         | Unicast       | Encrypted video frame (H.264/H.265/VP8 NAL unit) |
| 28    | VideoSignaling    | Unicast       | Video call setup: offer, answer, reject, bye, codec negotiation |
| 29    | WatchSync         | Unicast       | Synchronized playback command: play, pause, seek, speed |
| 30    | WatchReaction     | Multicast     | Timestamped emoji or voice reaction during watch-together |
| 31    | VideoFrame        | Unicast/SFU   | Group video frame (SFU relay distributes to participants) |
| 32    | ScreenShare       | Unicast       | Screen share frame (same pipeline as video, flagged separately) |
| 33    | WatchChunkRequest | Unicast       | Priority chunk request biased to playback position |
| 34    | TorrentMetadata   | Multicast     | BitTorrent .torrent file or magnet link metadata exchange |

### 2.6. Node Capabilities

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
| 8   | 256   | NearLink    | NearLink transport available |
| 9   | 512   | Video       | Video encoding/decoding capable |

---

## 3. Routing Algorithm

Aether uses a reactive routing protocol based on Ad-hoc On-demand Distance Vector (AODV) routing, extended with cryptographic route authentication and QoS-weighted route selection.

### 3.1. Route Request (RREQ)

When a node needs to send a packet to a destination for which it has no route, it initiates a Route Request:

1. The originator creates a `MeshPacket` with `Type = RouteRequest`, sets `SourceUhid` to itself, `DestinationUhid` to the target, and `TTL = 7` (the default).
2. The packet is broadcast to all directly connected peers.
3. Each intermediate node that receives an RREQ:
   a. Checks if it has already seen this RREQ by packet `Id`. If so, it silently drops the packet (deduplication). The deduplication cache holds up to `DeduplicationCacheSize` entries (default 10,000) and is fully cleared once the cap is reached.
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
- **RREQ dedup pruning:** The set of seen RREQ IDs is cleared when it exceeds `DeduplicationCacheSize` (default 10,000) entries.

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

> Reconciled 2026-05-05 against the C# reference implementation at
> `src/AetherNet.Security/Services/SignalProtocolService.cs` and the
> cross-language fixture corpus under `fixtures/signal/`. The C# reference
> ships full X3DH + Double Ratchet (Signal §3 + §5) over X25519. Go,
> Python, TypeScript, Rust, Swift, and Kotlin have been ported to the same
> envelope and are byte-equivalent at the X3DH and KDF_RK fixture level.
> C ships only the X25519 + KDF_RK + symmetric-ratchet primitives —
> sufficient for the fixture verifier, no full session machinery yet.
> Where this section disagrees with code, the code is authoritative;
> file an issue in `OPEN_ISSUES.md`.

Aether implements **X3DH** (Extended Triple Diffie-Hellman, Signal §3) for
asynchronous session establishment, immediately followed by the **Signal
Double Ratchet** (Signal §5) for ongoing forward secrecy and
post-compromise security. All session crypto runs over Curve25519:
**X25519** (RFC 7748) for ECDH and **Ed25519** (RFC 8032) for signing.

### 4.1. Identity Keys

Each node generates **two** long-term keypairs at first launch (no XEdDSA;
the simpler dual-key arrangement is what every implementation ships):

- **Ed25519 keypair** — 32-byte seed (private), 32-byte public key.
  Used for packet signing (§2.4), `SignedPreKeySignature` (§4.3),
  RREP authentication (§3.2), and tip signatures.
- **X25519 keypair** — 32-byte raw private and public keys. Used for
  the four X3DH DH operations (§4.4).

Reference: `SignalProtocolService.InitializeIdentityKeys`. Private keys
live on the device only; public keys are published in `PreKeyBundle`.

A 30-day P-256 → Ed25519 migration window is honoured for *signature
verification* on inbound packets only — see §7.5. Pre-key bundles
themselves are X25519-only on the wire.

### 4.2. Curve Choice

X3DH and the Double Ratchet use **X25519** exclusively. P-256 is *not*
used in session establishment by any current implementation. An earlier
draft of this spec described P-256 ECDH; that text predates the
2026-05-05 family-wide port to X25519 and is no longer accurate.

### 4.3. Pre-Key Bundle

A pre-key bundle is published so that an initiator can establish a
session without the responder being online (Signal §3.4):

```
PreKeyBundle {
    Uhid:                   string      // Node's Universal Hardware Identifier
    IdentityKey:            byte[32]    // Long-term Ed25519 public key (signing)
    IdentityKeyX25519:      byte[32]    // Long-term X25519 public key (ECDH)
    PreKeyId:               int32       // One-time pre-key id
    PreKey:                 byte[32]    // One-time pre-key X25519 public key (OPK)
    SignedPreKeyId:         int32       // Signed pre-key id
    SignedPreKey:           byte[32]    // Signed pre-key X25519 public key (SPK)
    SignedPreKeySignature:  byte[64]    // Ed25519(IdentityKey, SignedPreKey)
}
```

Reference: `AetherNet.Security.Models.PreKeyBundle`. Wire-shape contract is
the same across all 8 languages.

**One-time pre-key (OPK) pool.** Each responder maintains a pool of
`OpkPoolSize` (default 100, mirroring Signal's published guidance) X25519
OPKs. Bundle generation pops the next-unused id from a FIFO queue, then
tops the pool back up to its target size. Each OPK is consumed exactly
once: the responder removes and zeroises the private half on the first
PreKey message that references its id. Concurrent initiators racing for
the same OPK id will see exactly one `EstablishResponderSession` succeed
under `_preKeyLock`; the loser raises `CryptographicException`.

Reference: `SignalProtocolService.TopUpOpkPoolNoLock` (lines 494–518),
`SignalProtocolService.EstablishResponderSession` (lines 636–718). Pool
semantics are exercised by `tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`.

**Signed pre-key (SPK) rotation.** SPK is generated lazily on the first
bundle call and reused across subsequent calls so concurrent initiators
fetching bundles before X3DH runs do not invalidate each other's bundles.
Periodic SPK rotation (Signal §3.3 recommends weekly) is an explicit
operation, not a side effect of bundle generation.

Pre-key ids are drawn from `RandomNumberGenerator.GetInt32(1, int.MaxValue)`
with explicit collision retry (up to 64 attempts before raising).

### 4.4. Session Establishment (X3DH)

The full X3DH (Signal §3.3) runs on the initiator side. Four DH
operations are computed over X25519:

```
DH1 = DH(IK_A, SPK_B)    // long-term mutual auth
DH2 = DH(EK_A, IK_B)     // initiator ephemeral binds responder identity
DH3 = DH(EK_A, SPK_B)    // initiator ephemeral binds responder SPK
DH4 = DH(EK_A, OPK_B)    // initiator ephemeral binds responder OPK
```

where `IK_A` / `IK_B` are the X25519 identity keys, `EK_A` is a fresh
X25519 ephemeral generated for this session only, `SPK_B` is the
responder's signed pre-key, and `OPK_B` is the responder's one-time
pre-key. The initial root key is:

```
RK_0 = HKDF-SHA256(
    ikm  = DH1 || DH2 || DH3 || DH4,
    salt = (default — empty),
    info = UTF8("aether-x3dh-root-v1"),
    L    = 32 bytes)
```

The `info` constant `aether-x3dh-root-v1` is identical across every
implementation and is pinned by `fixtures/signal/expected/x3dh_basic.json`
(field `root_key_hex`).

Reference: `SignalProtocolService.ProcessPreKeyBundleAsync` (lines
554–626). Verification path:
`fixtures/signal/inputs.json` case `x3dh_basic` →
`fixtures/signal/expected/x3dh_basic.json`.

**Bundle verification.** Before any DH runs, the initiator verifies
`SignedPreKeySignature` against `IdentityKey` using Ed25519. A failed
verification raises `CryptographicException` and the bundle is dropped.
Public-key sizes are validated against `X25519Service.PublicKeySize` (32);
malformed bundles are rejected.

**Session priming.** At the end of `ProcessPreKeyBundleAsync` a
`SignalSession` is created with:

- `RootKey = RK_0`
- `MyEphemeralPriv / MyEphemeralPub = EK_A` — Signal-canonical X3DH ↔
  Double-Ratchet integration: the initiator's X3DH ephemeral becomes its
  first DH-ratchet keypair (`DHs`).
- `RemoteEphemeralPub = SPK_B` — the responder's signed pre-key is
  treated as the initial peer ratchet key (`DHr`).
- `SendChainKey = null`, `RecvChainKey = null` — both chain keys are
  derived lazily on first send / first DH-ratchet receive.
- `PendingPreKeyMessage = true` — flags that the next outbound
  `EncryptAsync` call MUST emit a PreKey message (`MessageType=1`).

All DH outputs and the concatenated shared secret are zeroised in the
`finally` block via `CryptographicOperations.ZeroMemory`.

**Refusing to send insecurely.** If `EncryptAsync` is called for a peer
with no session, the call throws `InvalidOperationException`. There is no
UHID-derived fallback path. Hosts are expected to queue the message
(see `MessagingService` + `SignalMessageEnvelopeCipher`) and retry once
session establishment completes.

### 4.5. Double Ratchet (Signal §5)

Each side maintains a rotating X25519 ratchet keypair (`DHs`) and a copy
of the peer's last-seen ratchet public key (`DHr`). On every message the
sender publishes its current `DHs` public; whenever the receiver
observes a new `DHr`, it runs a **DH-ratchet step** that re-keys the
chain via `KDF_RK(RK, DH(myDHs, newDHr))` — re-deriving both the root key
and a fresh chain key.

#### 4.5.1. KDF_RK

`KDF_RK` is HKDF-SHA256 over a 64-byte block, split 32+32 into the new
root key and the new chain key:

```
out      = HKDF-SHA256(
    ikm  = DH_output,
    salt = current_root_key,
    info = UTF8("aether-ratchet-rk-v1"),
    L    = 64 bytes)
new_RK   = out[0..32]
new_CK   = out[32..64]
```

Reference: `SignalProtocolService.KdfRk` (lines 857–868). Pinned by
`fixtures/signal/inputs.json` case `kdf_rk_basic` →
`fixtures/signal/expected/kdf_rk_basic.json`.

#### 4.5.2. Symmetric Ratchet

Per Signal §5.1, message keys and chain keys are derived from a chain
key using HMAC-SHA256 with single-byte domain separation:

```
message_key   = HMAC-SHA256(chain_key, 0x01)
new_chain_key = HMAC-SHA256(chain_key, 0x02)
```

Reference: `SignalProtocolService.RatchetChainKey` (lines 876–881).
Pinned by `fixtures/signal/inputs.json` cases `ratchet_step_basic` and
`ratchet_step_three_iterations`.

The earlier draft of this spec described `messageKey =
HMAC-SHA256(chain_key, counter_bytes)` and a separate `chain_key
advance via HMAC(chain_key, 0x01)`. That was non-Signal and never
implemented; it has been replaced with the canonical 0x01/0x02 split.

#### 4.5.3. DH-Ratchet Step on Receive

Triggered when the inbound message's `SenderEphemeralKeyX25519` differs
from the cached `RemoteEphemeralPub` (constant-time compare).

1. Save outbound counter as `PreviousChainCount` (Signal §5: PN) so the
   peer can compute skipped keys across the boundary.
2. Reset `SendCounter` and `RecvCounter` to 0; install the new
   `RemoteEphemeralPub`.
3. Derive new receiving chain: `(RK', CKr) = KDF_RK(RK, DH(myDHs, newDHr))`.
4. Zeroise the old `myDHs` private; generate a fresh X25519 keypair.
5. Derive new sending chain: `(RK'', CKs) = KDF_RK(RK', DH(newDHs, newDHr))`.

Reference: `SignalProtocolService.DhRatchetReceive` (lines 726–772).

#### 4.5.4. Lazy Sending-Chain Derivation

The initiator's first send runs a **half-step** rather than a full
DH-ratchet — the X3DH already placed `DHs` and `DHr`, so only the
sending chain needs deriving:

```
(RK', CKs) = KDF_RK(RK, DH(myDHs, DHr))
```

`DHs` is *not* rotated here. It is rotated only on a true receive-side
DH-ratchet step.

Reference: `SignalProtocolService.DhRatchetSendOnly` (lines 780–796).

#### 4.5.5. Skipped Message Keys

When messages arrive out of order, each skipped counter's message key is
cached in `SkippedMessageKeys`, keyed by `(Hex(remoteEphPub):counter)`.
The remote-pub binding is essential — out-of-order messages from a prior
chain (different `DHr`) can still arrive after a DH-ratchet step and
need their own per-chain key set.

Limits:

- Skipping more than `MaxSkippedKeys` (1000) entries in a single gap
  raises `CryptographicException` and forces session re-establishment.
- Crossing a DH-ratchet boundary, the receiver first skips up to
  `PreviousChainCount` keys on the *old* chain, then runs the
  DH-ratchet step before deriving keys on the new chain.

Reference: `SignalProtocolService.SkipMessageKeys` (lines 804–830) and
the in-decrypt skip loop (lines 366–388).

### 4.6. Encrypted Payload Format

```
EncryptedPayload {
    Ciphertext:                     byte[]      // AES-256-GCM ciphertext || 16-byte tag
    Nonce:                          byte[12]    // AES-GCM nonce, freshly random
    MessageType:                    int32       // 0 = normal, 1 = PreKey
    SenderUhid:                     string      // Sender's UHID
    Counter:                        int32       // Sender's Ns within current chain

    // Double Ratchet — populated on EVERY message:
    SenderEphemeralKeyX25519:       byte[32]    // Sender's current DHs public
    PreviousChainCount:             int32       // Signal §5: PN

    // X3DH — populated only on PreKey messages (MessageType == 1):
    InitiatorIdentityKeyX25519:     byte[32]?   // Initiator's IK_X25519 public
    UsedSignedPreKeyId:             int32       // SPK id consumed
    UsedOneTimePreKeyId:            int32       // OPK id consumed
    InitiatorEphemeralKeyX25519:    byte[32]?   // DEPRECATED — equals SenderEphemeralKeyX25519
}
```

Reference: `AetherNet.Security.Models.EncryptedPayload` (lines 55–66 of
`SecurityModels.cs`). The `InitiatorEphemeralKeyX25519` field is a
backward-compat alias for the pre-Double-Ratchet wire envelope and
equals `SenderEphemeralKeyX25519` on PreKey messages; new consumers
should ignore it.

AES-GCM parameters: 256-bit key, 96-bit nonce (`AesNonceSize = 12`),
128-bit tag (`AesTagSize = 16`), tag concatenated to ciphertext.
Message keys are zeroised in `finally` blocks immediately after AES-GCM
encrypt/decrypt.

### 4.7. Per-Language Status

| Language    | X3DH (4 DHs) | Double Ratchet | OPK pool       | Fixture-verified |
|-------------|--------------|----------------|----------------|------------------|
| C# (.NET)   | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Go          | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Python      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| TypeScript  | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Rust        | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Swift       | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| Kotlin      | full         | full (§5)      | pool, default 100 | x3dh_basic, ratchet_*, kdf_rk_basic |
| C           | primitives only — `aethernet_x25519_*`, `aethernet_signal_kdf_rk` | not implemented | — | kdf_rk_basic only |

All 7 session-capable languages (C# + Go + TypeScript + Python + Kotlin + Swift + Rust) ship the 100-key FIFO OPK pool with lazy top-up and lock-protected consumption, matching the C# reference contract. C ships primitives only; full session machinery is tracked in `OPEN_ISSUES.md` item 11.

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
5. **Fallback:** If no local transport can reach the target, the packet is queued for server relay via AetherNetAPI.

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
- RREQ deduplication by packet ID prevents amplification through broadcast storms. The dedup cache is flushed when it exceeds `DeduplicationCacheSize` (default 10,000) entries.
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
   - **API call:** Sent to AetherNetAPI for server-side distribution and bridging to PanikAPI (SMS/email dispatch).
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
3. **Server relay attempt:** If mesh routing fails, the sender attempts to relay through AetherNetAPI. If the server can reach the recipient (or queue the message), delivery succeeds.
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
4. The receipt is also synced to AetherNetAPI for analytics.

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

## 10. Video Streaming

> **Status as of 2026-05-05 — design + C# scaffolding, no shipping codec
> pipeline.** The packet types `StreamAnnounce` (11), `StreamSegment` (12),
> `StreamSubscribe` (13), `StreamUnsubscribe` (14), `VideoCall` (27),
> `VideoSignaling` (28), `VideoFrame` (31), `ScreenShare` (32) are
> wire-defined and round-trip via the cross-language fixture corpus.
> The C# `AetherNet.Streaming` module ships interfaces, models, and skeleton
> services (`StreamingService`, `VideoCallService`, `WatchTogetherService`)
> that wire up routing/DI seams and unicast segment fan-out — but no actual
> video encode/decode is bound to them. The other 7 languages have wire
> types only. The forward-design doc at
> `docs/adaptive-secure-streaming-spec.md` is the target architecture.
> Treat the prose below as the specification of what those services WILL
> implement; consult `OPEN_ISSUES.md` for production-readiness gaps.


Aether supports three video modes: peer-to-peer video calls, group video (unlimited participants with dynamic topology), and live broadcast. All video frames are encrypted with Signal Protocol and signed with Ed25519.

### 10.1. Transport Capability Matrix

Before initiating a video call, the originator queries the transport layer to determine the best available connection to the peer. The transport determines what quality of video is possible:

| Transport | Video Support | Max Resolution | Recommended Codec | Max Bitrate | Watch-Together |
|-----------|--------------|----------------|-------------------|-------------|----------------|
| BLE | No (audio-only) | — | — | 64 Kbps | Sync packets only |
| NearLink | Light | 360p | H.265 | 800 Kbps | SharedFile + StreamFromHost |
| WiFi Direct | Full | 1080p | H.264 | 3000 Kbps | All modes |
| Internet | Full | 720p | H.264 | 1500 Kbps | All modes |
| CircleLink | No (audio-only) | — | — | 64 Kbps | Sync packets only |

If the only available transport is BLE or CircleLink, the video call service automatically downgrades to a voice call.

### 10.2. Video Codecs

| Enum Value | Codec | Use Case |
|------------|-------|----------|
| 0 | H.264 | Default. Widely supported, good compression. |
| 1 | H.265 | Better compression. Used on NearLink (bandwidth-constrained). |
| 2 | VP8 | Royalty-free alternative. |

### 10.3. Video Resolutions

| Enum Value | Resolution | Typical Bitrate |
|------------|-----------|-----------------|
| 0 | AudioOnly | 64 Kbps (Opus) |
| 1 | 360p | 800 Kbps |
| 2 | 480p | 1200 Kbps |
| 3 | 720p | 1500 Kbps |
| 4 | 1080p | 3000 Kbps |

### 10.4. P2P Video Call Flow

1. **Capability check**: Originator queries `GetVideoCapabilityAsync(peerUhid)` to determine the best transport, max resolution, and recommended codec.
2. **Offer**: Originator sends a `VideoSignaling` packet (type 28) with `SignalType = Offer`, including preferred codec, max resolution, and max bitrate.
3. **Answer/Reject**: Callee responds with `SignalType = Answer` (negotiating codec to lowest common denominator) or `SignalType = Reject`.
4. **Active call**: Both nodes exchange `VideoCall` packets (type 27) containing H.264/H.265/VP8 NAL units. Each frame includes a sequence number for jitter buffer ordering and a keyframe flag.
5. **Screen share**: Either party can toggle screen sharing. `VideoSignaling` with `SignalType = ScreenShareStart/Stop` notifies the peer. Screen share frames use `PacketType.ScreenShare` (type 32) but the same processing pipeline.
6. **End call**: Either party sends `VideoSignaling` with `SignalType = Bye`.

All signaling and frame payloads are encrypted with Signal Protocol (X3DH session). The encrypted payload is serialized as JSON-encoded `EncryptedPayload` within the `MeshPacket.Payload` field.

### 10.5. Video Call State Machine

```
  Initiating ──► Ringing ──► Active ──► Ended
                   │                      ▲
                   ├──► Rejected ─────────┘
                   └──► Failed ───────────┘
```

States: `Initiating(0)`, `Ringing(1)`, `Active(2)`, `OnHold(3)`, `Ended(4)`, `Failed(5)`, `Rejected(6)`.

### 10.6. Group Video

Group video sessions support unlimited participants. The topology is dynamically selected based on participant count:

- **FullMesh** (2-3 participants): Each participant sends one stream to every other participant. Simple, low latency.
- **SFU** (4+ participants, threshold: `SfuThresholdParticipants = 4`): One node is elected as the SFU relay. Each participant sends one stream to the relay, which distributes it to all others. The relay node earns tips via the incentive layer.

Topology switches are automatic: when the 4th participant joins, the session transitions from FullMesh to SFU. When participants leave and the count drops below 4, it transitions back.

Group video frames use `PacketType.VideoFrame` (type 31). In SFU mode, frames are sent to the relay node's UHID, which re-broadcasts them.

### 10.7. Jitter Buffer

The video jitter buffer operates independently from the voice jitter buffer (which handles 20ms Opus frames):

- **Range**: 60ms minimum, 500ms maximum.
- **Adaptive depth**: Tracks inter-frame jitter via Exponential Moving Average (EMA). Buffer depth = 2× jitter estimate, clamped to [60, 500] ms.
- **Keyframe-aware dropping**: When the buffer overflows, non-keyframe (P/B) frames are dropped first. I-frames (keyframes) are never dropped — they are required for decoder recovery.
- **Gap handling**: When a sequence gap is detected, the buffer skips to the next available keyframe rather than waiting indefinitely.

### 10.8. Video Signaling Types

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Offer | Video call initiation with codec/resolution preference |
| 1 | Answer | Call acceptance with negotiated parameters |
| 2 | Reject | Call rejection |
| 3 | Bye | Call termination |
| 4 | Upgrade | Request higher quality (e.g., transport improved) |
| 5 | Downgrade | Request lower quality (e.g., bandwidth drop) |
| 6 | ScreenShareStart | Peer began sharing screen |
| 7 | ScreenShareStop | Peer stopped sharing screen |

### 10.9. Encryption Model

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| P2P video call | Signal Protocol per frame | X3DH key agreement |
| Group video | Group channel key (AES-GCM) | Distributed via Signal Protocol at session creation |
| Screen share | Same as parent call mode | Inherited from video call session |

---

## 11. Watch Together

> **Status as of 2026-05-05 — design + C# scaffolding, same maturity as
> § 10.** Packet types `WatchSync` (29), `WatchReaction` (30),
> `WatchChunkRequest` (33), `TorrentMetadata` (34) are wire-defined and
> fixture-tested. `AetherNet.Streaming.WatchTogetherService` provides the
> coordination skeleton (session state, sync command propagation via
> `IMeshSender`, RTT-compensation helpers); BitTorrent ingest, ChipIn
> SDPKT settlement, and chunk-fetch-from-peers are not implemented in any
> language. Treat the prose below as the target protocol; the forward
> design doc at `docs/adaptive-secure-streaming-spec.md` covers the same
> ground in more detail.


Watch Together enables synchronized media playback across a group of mesh peers. The host has exclusive control over playback (play, pause, seek, speed). Sync commands include wall clock timestamps for RTT compensation.

### 11.1. Watch Modes

| Enum Value | Mode | Data Flow | Transport Requirement |
|------------|------|-----------|----------------------|
| 0 | SharedFile | Sync packets only (< 100 bytes each) | Any (works over BLE) |
| 1 | StreamFromHost | P2P chunk transfer (reuses P2pContentService) | WiFi Direct or Internet |
| 2 | BitTorrent | Mesh + external swarm via gateway nodes | WiFi Direct or Internet |

### 11.2. SharedFile Mode

Both participants have the same file (matched by SHA-256 content hash). Only `WatchSync` packets are exchanged. This is the most bandwidth-efficient mode and works over BLE.

1. Host creates a watch session with `contentHash` (SHA-256 of the file).
2. Participants join and report `IsReady = true` when their player is loaded.
3. Session starts when ALL participants report ready.
4. Host sends play/pause/seek/speed commands as `WatchSync` packets (type 29).
5. Receivers apply RTT compensation: `adjustedPosition = commandPosition + (wallClockNow - commandWallClock) / 2`.

### 11.3. StreamFromHost Mode

Only the host has the file. The host generates a `ContentManifest` (reusing the P2P content system) and participants download chunks via the mesh.

- Chunk selection uses `SequentialFromPosition` strategy (not `RarestFirst`): prioritizes chunks ahead of the current playback position, then backfills for seeding.
- Buffer target: 30 seconds ahead (`WatchTogetherBufferAheadSeconds`).
- Auto-pause: If ANY participant's buffer drops below 10 seconds (`WatchTogetherMinBufferSeconds`), the session auto-pauses all participants with a `BufferUnderrun` sync command. Playback resumes when all participants have sufficient buffer (`BufferReady`).
- As viewers download chunks, they become seeders for other viewers (BitTorrent-style swarming within the mesh).

### 11.4. BitTorrent Mode

A participant shares a `.torrent` file or magnet link in the group chat. The `TorrentMetadata` packet (type 34) distributes the torrent info to all session participants.

**Mesh-to-Swarm Bridge:**
- Gateway nodes (nodes with internet) download pieces from the external BitTorrent swarm.
- Gateway nodes re-encrypt downloaded pieces for mesh distribution and seed to mesh peers.
- Mesh peers without internet receive pieces from gateway nodes and from each other.
- The P2P content engine translates between BitTorrent's piece model and Aether's chunk model.

Once enough content is buffered, watch-together playback begins using the same sync protocol as SharedFile mode.

### 11.5. Watch Session State Machine

```
  WaitingForReady ──► Playing ◄──► Paused
        │                │           │
        │                ▼           │
        │            Buffering ──────┘
        │                │
        └────────────► Ended
```

States: `WaitingForReady(0)`, `Buffering(1)`, `Playing(2)`, `Paused(3)`, `Ended(4)`.

### 11.6. Sync Command Types

| Enum Value | Type | Description |
|------------|------|-------------|
| 0 | Play | Resume playback at specified position |
| 1 | Pause | Pause at specified position |
| 2 | Seek | Jump to specified position |
| 3 | Speed | Change playback speed |
| 4 | BufferUnderrun | Auto-pause — a participant's buffer is critically low |
| 5 | BufferReady | Resume — all participants have sufficient buffer |

### 11.7. RTT Compensation

Sync commands include a `WallClockMs` field (Unix epoch milliseconds). When a receiver processes a sync command:

1. `rtt = receiverWallClock - commandWallClock`
2. `networkDelay = rtt / 2`
3. For Play and BufferReady commands: `adjustedPosition = commandPosition + networkDelay`
4. For Pause and Seek commands: position is applied exactly (no adjustment needed since playback is stopping/jumping).

This ensures all participants are synchronized within half the network RTT.

### 11.8. Reactions

Participants can react to the content during playback:

- **Emoji reactions**: `WatchReaction` packet (type 30) with `Type = Emoji`, carrying the emoji string and the media position at the time of reaction.
- **Voice comments**: `WatchReaction` packet with `Type = VoiceComment`, carrying Opus-encoded audio data (maximum 10 seconds). Voice data is included in the reaction's `VoiceData` field.

Reactions are broadcast to all session participants. They are timestamped to the media position, allowing replay-synchronized display.

### 11.9. ChipIn — Group Content Acquisition

ChipIn enables group members to pool funds (in ZAR, settled via SDPKT wallets through LedgerAPI) to collectively acquire content for group watching.

**State machine:**
```
  Collecting ──► Funded ──► Purchasing ──► Acquired
       │                        │
       └── (timeout) ──► Failed/Refunded
```

States: `Collecting(0)`, `Funded(1)`, `Purchasing(2)`, `Acquired(3)`, `Failed(4)`, `Refunded(5)`.

**Flow:**
1. Initiator creates a `ChipInPool` with target amount and content description.
2. Participants contribute amounts via SDPKT wallet transactions.
3. When `CollectedAmount >= TargetAmount`, state transitions to `Funded`.
4. The system acquires the content (e.g., initiates a BitTorrent download).
5. Once content is available, state transitions to `Acquired` and watch-together can begin.

Each contribution is recorded with a SDPKT transaction ID for audit trail.

### 11.10. Encryption Model

| Mode | Encryption | Key Distribution |
|------|-----------|-----------------|
| Watch sync commands | Channel/conversation key | Existing Signal Protocol session |
| Content chunks (StreamFromHost) | Content key per manifest | Distributed via Signal Protocol |
| BitTorrent pieces | Re-encrypted on ingest | Gateway downloads cleartext from swarm, encrypts for mesh |
| Watch reactions | Session key | Derived from conversation key |

### 11.11. Feature Flags

All video and watch-together features are gated behind feature flags (all disabled by default):

| Flag | Parent | Description |
|------|--------|-------------|
| AETHERNET_VIDEO_CALL | AETHERNET_VOICE | P2P and group video calling |
| AETHERNET_VIDEO_GROUP | AETHERNET_VIDEO_CALL | Multi-party video sessions |
| AETHERNET_SCREEN_SHARE | AETHERNET_VIDEO_CALL | Screen sharing in video calls |
| AETHERNET_WATCH_TOGETHER | AETHERNET_CONTENT_P2P | Synchronized media playback |
| AETHERNET_WATCH_REACTIONS | AETHERNET_WATCH_TOGETHER | Emoji and voice reactions |
| AETHERNET_TORRENT_INGEST | AETHERNET_CONTENT_P2P | BitTorrent file acceptance for mesh distribution |

Feature flags have parent dependencies: a child flag can only be enabled if its parent is also enabled. This allows progressive rollout.

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
| AetherNetBleServiceUuid      | A3E7-1001-0001-0000-000000000000 |

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
| SosPriority                | 255   |
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
| DefaultChunkSizeBytes     | 8192    |
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

### Video
| Constant                       | Value |
|--------------------------------|-------|
| VideoFrameDurationMs           | 33    |
| VideoJitterBufferMinMs         | 60    |
| VideoJitterBufferMaxMs         | 500   |
| WatchTogetherBufferAheadSeconds| 30    |
| WatchTogetherMinBufferSeconds  | 10    |
| NearLink360pBitrateKbps       | 800   |
| Internet1080pBitrateKbps      | 3000  |
| SfuThresholdParticipants       | 4     |
| ScreenShareFrameDurationMs     | 100   |

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
| **SFU** | Selective Forwarding Unit. A relay node that receives one video stream from each sender and distributes it to all other participants, reducing per-node upload bandwidth. |
| **ChipIn** | Group funding mechanism where participants pool SDPKT funds to collectively acquire content for group watching. |
| **NAL** | Network Abstraction Layer. The encapsulation format used by H.264 and H.265 codecs to packetize video frames. |

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
