# Aether Protocol — Threat Model

**Reviewed against HEAD `b8b3d22` (2026-05-06).** This document describes
what the cryptographic protocol layer of `aether-protocol` defends against,
what is explicitly out of scope, and the assumptions the security claims
rely on. It is intentionally honest: an attacker who reads this should be
able to enumerate every attack the protocol does **not** stop, and should
not be misled by the marketing on the README.

The companion document is [`PROTOCOL_SPEC.md`](PROTOCOL_SPEC.md) §7
(Security Model). Where the two diverge, the implementation in
`src/AetherNet.Security/` is authoritative.

---

## 1. Scope

### What `aether-protocol` IS

A Signal-Protocol-style end-to-end-encrypted messaging library plus a
mesh-networking primitive (AODV-style routing + DTN store-and-forward + SOS
flood). The core security guarantees are:

1. **Confidentiality** — message bodies are AES-256-GCM-encrypted under
   per-message keys derived from a Double Ratchet (Signal §5).
2. **Authenticity** — every `MeshPacket` carries an Ed25519 signature over a
   canonical signable-data buffer (PROTOCOL_SPEC §2.4).
3. **Replay protection** — packets are dropped on duplicate
   `(SourceUhid, PacketNonce)` within a 5-minute freshness window.
4. **Forward and post-compromise secrecy** — the Double Ratchet rekeys on
   every roundtrip's DH-pubkey change; an attacker that compromises a
   session key recovers neither past nor future messages.

### What `aether-protocol` is NOT

- **Not a transport-layer security replacement.** Use TLS for client→server.
  Aether's E2EE is for peer-to-peer mesh traffic; the moment a packet
  exits the mesh into a centralised backend, that backend's transport
  security is the host's responsibility.
- **Not a key management system.** The host supplies durable storage for
  identity and pre-key material via `IPreKeyStore` (or any
  `IKeyValueStore`-backed adapter). Hardware keystore integration, TPM
  attestation, key-escrow recovery, and encryption-at-rest are all the
  host's job.
- **Not an authentication system.** Aether authenticates that "the holder
  of identity-key-X said this packet". Mapping identity-key-X to "the
  human Alice" is the host's UX responsibility (safety-number comparison,
  out-of-band fingerprint exchange, prior trust chain).
- **Not a privacy network.** The wire reveals message-type, packet length,
  source UHID, destination UHID, hop count, and timing. It is not Tor.

---

## 2. Defended attacks

### 2.1. Eavesdropping in flight

Every payload is encrypted with AES-256-GCM under a per-message key derived
from the Double Ratchet's symmetric chain (Signal §5.1, HMAC-SHA256 with
`0x01`/`0x02` domain separation). An attacker that captures every packet
between Alice and Bob recovers nothing without one of their session keys.

Verified by `tests/AetherNet.Security.Tests/SignalProtocolEncryptionTests.cs`
and the cross-language `fixtures/signal/expected/ratchet_step_basic.json`
vectors.

### 2.2. Message forgery

Every Wave-2 packet carries an Ed25519 signature over the canonical
`BuildSignableData(packet)` buffer (`src/AetherNet.Security/Services/PacketSigningService.cs`,
PROTOCOL_SPEC §2.4). Forged packets fail verification and are dropped at
every hop that knows the source's identity public key. Route Reply packets
(RREP) are signed by the claimed destination — intermediate nodes cannot
impersonate destinations because they do not hold the destination's
Ed25519 private key.

### 2.3. Replay attacks

`PacketSigningService.VerifyPacketAsync`:

- Rejects packets whose `TimestampMs` is more than 5 minutes off local UTC
  (`FreshnessWindowMs = 5 * 60 * 1000`).
- Maintains an in-memory dedup map keyed by `(SourceUhid, PacketNonce)`
  with a 5-minute TTL. The dedup key was changed from `nonce` alone to
  `(source, nonce)` in commit `5bd52a9` to fix two failure modes:
  cross-sender nonce collisions dropping legitimate traffic, and
  pre-registration attacks where an adversary plants a nonce against a
  recipient to block the legitimate sender's first packet.

Counters: `aethernet.nonces.replayed`, `aethernet.timestamps.stale`.

### 2.4. Forward secrecy (past-key compromise)

The Double Ratchet derives a new sending chain key on every DH-rotation
step (KDF_RK, HKDF-SHA256 over `salt = current_root_key`,
`info = "aether-ratchet-rk-v1"`, 64-byte block split 32+32 into new
root and chain keys — `src/AetherNet.Security/Services/SignalProtocolService.cs`).
An attacker that compromises the current session state cannot decrypt any
prior message: each prior message key was derived and zeroed
(`CryptographicOperations.ZeroMemory`) before the next ratchet step.

### 2.5. Post-compromise security (future-key recovery)

When the receiving side observes a new `SenderEphemeralKeyX25519` on an
inbound message, it runs a DH-ratchet step on receive (Signal §5.2). The
attacker's cached session state goes stale on the very next roundtrip; an
attacker that snapshots a session and steps away can no longer decrypt
messages once the legitimate parties have exchanged one round.

DH-rotation step on receive landed across all 8 languages — see
`OPEN_ISSUES.md` item 2 for the family-wide commit list.

### 2.6. One-time pre-key replay

Each one-time pre-key (OPK) is consumed exactly once. The C# reference
ships a 100-OPK pool with FIFO issue, lazy top-up on every bundle
generation, and lock-protected single-shot consumption
(`SignalProtocolService.TopUpOpkPoolNoLock`, verified by
`tests/AetherNet.Core.Tests/PreKeyPoolTests.cs`). An OPK is removed and
zeroed the moment the responder consumes it during X3DH, so a replayed
PreKey message that re-uses the same OPK id cannot establish a session.

The other 7 languages still issue a single OPK per session — functionally
correct for sequential workloads but exposes a concurrency hazard under
simultaneous bundle fetches. Tracked as `OPEN_ISSUES.md` §9.

### 2.7. Cross-language wire drift

Every implementation must produce byte-identical outputs against the
fixture corpus under `fixtures/`:

- `fixtures/expected/*.bin` — 10 packet-serialization fixtures, 122
  cross-language byte-equality assertions in CI.
- `fixtures/signal/expected/x3dh_basic.json` — X3DH math (4 X25519 DHs,
  HKDF-SHA256 root with `info = "aether-x3dh-root-v1"`).
- `fixtures/signal/expected/ratchet_step_basic.json`,
  `ratchet_step_three_iterations.json` — symmetric ratchet KDFs.
- `fixtures/signal/expected/kdf_rk_basic.json` — DH-ratchet step.

A drift in any language's HKDF info string, byte order, or padding fails
its `SignalFixtureTests` build. Wire-compatible interop is therefore a
build-time invariant, not a runtime hope.

### 2.8. Static-static DH compromise (the earlier broken X3DH)

Pre-2026-05-05, the C# `KEY_EXCHANGE` implementation used the local
node's identity key for both DH operations — a static-static collapse
that broke the X3DH ephemeral-key forward-secrecy property. Closed by
commit `07a93f5`: real X3DH now performs the canonical 4 DHs
`DH(IK_A,SPK_B) || DH(EK_A,IK_B) || DH(EK_A,SPK_B) || DH(EK_A,OPK_B)`
with a fresh per-session ephemeral. See `OPEN_ISSUES.md` §1.

### 2.9. Routing loops and broadcast storms

`RoutingService` deduplicates RREQ packets by `(originUhid, broadcastId)`
in a bounded cache (default 10 000 entries; `ProtocolConstants.RouteRequestDedupCacheSize`).
TTL is decremented at every hop and packets with `Ttl == 0` are dropped.
SOS broadcasts are rate-limited to 3/hour per origin and self-origin
suppression prevents a node from rebroadcasting its own SOS.

### 2.10. DoS via OPK pool exhaustion

The OPK pool is bounded (`OpkPoolSize`, default 100) and the Signal health
check raises `Unhealthy` when available OPKs drop below
`SignalOptionsBag.MinAvailableOpks` (default 10). Hosts wire alerting on
the `aether-signal` health status. An attacker that drains OPKs by
fetching bundles cannot exceed the configured pool size; the responder's
X3DH continues to work for already-issued bundles and recovers as the
top-up runs on the next bundle generation.

### 2.11. Passive BLE device-tracking

A passive scanner that logs a stable BLE MAC or Service UUID can follow a
device across time and place. `BlePrivacy`
(`src/AetherNet.Security/Privacy/BlePrivacy.cs`) closes the identifier-linkage
vector: the advertised Service UUID is re-derived every 15 minutes as
`HMAC-SHA256(rotation_key, window)` (PROTOCOL_SPEC §12.3), and peers are
addressed by resolvable private addresses (IRK + `ah`) rather than a fixed MAC.
Without the rotation key or IRK, two advertisements cannot be linked. Pinned to
`fixtures/bleprivacy/`.

**Residual risk.** This closes only the BLE-identifier vector — it does **not**
make Aether a privacy network (§1). Once a packet is on the mesh the cleartext
`MeshPacket` header still exposes source/destination UHID, type, length, and
timing (traffic analysis stays out of scope, §3.3), and RF-layer fingerprinting
is unaddressed. Emitting the rotating identifiers on-air is the host BLE stack's
job — the library only derives them.

### 2.12. Coerced key disclosure (duress)

An adversary with physical possession who coerces the user into unlocking.
`PanicWipe` (`src/AetherNet.Security/Privacy/PanicWipe.cs`) accepts a **duress
PIN** — matched against a stored `SHA-256(pin)` in constant time (no early-exit
timing leak) — that securely erases every identity key (overwrite-with-random,
then zero) across the key-name manifest, so the surrendered device holds no
usable identity. Pinned to `fixtures/panicwipe/`.

**Residual risk.** Best-effort and explicitly bounded: it does **not** defend
against a forensic image captured *before* the wipe, flash wear-levelling that
preserves a prior copy of the key bytes, an adversary who compels the *genuine*
PIN, or coercion after messages were already read. Constant-time compare
mitigates PIN-guess timing, not a full side-channel adversary (§3.2).

### 2.13. Loss of the only device (recovery)

Not an attacker, but the availability failure of losing the sole copy of an
identity. Recovery-phrase backup (`src/AetherNet.Security/Backup/`) encodes the
32-byte Ed25519 identity seed as a checksummed 24-word BIP-39 phrase
(PROTOCOL_SPEC §12.4) that restores the identity on any device — no server or
custodian holds it.

**Residual risk — a new theft surface.** The phrase **is** the identity: anyone
who reads the 24 words can fully impersonate the user, with no revocation. It
trades a device-loss risk for a paper-secret risk. The library encodes/decodes
and checksums the phrase; secure display, storage, and the optional BIP-39
passphrase are the host's responsibility.

### 2.14. Rogue device injection into multi-device sync

An attacker who tries to insert a device they control into a victim's sync set,
or to forge sync records. A `DeviceLink` (`src/AetherNet.Security/Sync/`) is
**Ed25519-signed by the identity key** (PROTOCOL_SPEC §12.1), so only the
identity holder can authorise a new device — an unsigned or wrong-key link fails
verification. `SyncRecord` payloads travel E2E-encrypted inside the DTN/mesh
path, so relays carry but cannot read them. Pinned to `fixtures/sync/`.

**Residual risk.** This authenticates the *linking*, not the linked device's
later behaviour: a device that is legitimately linked and *then* compromised
sees all synced state — sync has no per-record forward secrecy. Reconciliation
is last-write-wins on `(created_at_ms, logical_clock, device_id, record_id)`, so
a linked device with a skewed clock can bias which record wins; clock integrity
is the host's concern. Signature-byte parity carries the Swift/CryptoKit
exception noted in PROTOCOL_SPEC §12.1.

---

## 3. Out of scope

These are real attacks the protocol does **not** stop. Some are theoretically
mitigable in a future release; others are fundamentally a host concern.

### 3.1. Endpoint compromise

If an attacker has root on Alice's device they can read her identity-key
private bytes from memory and decrypt every session she holds. The
protocol assumes the device's process memory is trusted. Mitigations
(platform key-store, SGX, hardware-backed keystores) are explicitly the
host's responsibility — see Section 4.

### 3.2. Side-channel attacks

The reference implementation uses
`CryptographicOperations.FixedTimeEquals` for ratchet-pubkey comparison
(`SignalProtocolService.ConstantTimeEquals`) but is not specifically
hardened against:

- Timing side channels in AES-GCM (the .NET BCL `AesGcm` is hardware-
  accelerated on AES-NI capable CPUs; software fallback timing is not
  audited).
- Power-analysis side channels (purely software — no hardware
  countermeasures).
- Cache-timing on key-derivation paths (HKDF-SHA256 via the BCL).

A nation-state-grade lab attack on a stolen unlocked device is plausible.

### 3.3. Traffic analysis

The wire format reveals:

- Packet **type** (1 byte at offset 1 — RREQ vs Data vs SOS is in the
  clear).
- Packet **length** (payloads are not padded).
- **Source and destination UHIDs** (UTF-8, in the clear).
- **Timestamps**, **TTL**, and **priority**.

Padding, cover traffic, and onion routing are not implemented. An
adversary that can passively observe BLE / Wi-Fi traffic can build a
contact graph and a timing profile of every conversation, even though
they cannot read the content. This is a known limitation; mitigation
would require a wire-format break and is not on the current roadmap.

### 3.4. Quantum attacks

X25519 (RFC 7748) and Ed25519 (RFC 8032) both break under a
sufficiently large quantum computer running Shor's algorithm. The
protocol is **not post-quantum**. A future migration to a hybrid
Kyber + X25519 / Dilithium + Ed25519 scheme is a known concern but is
not scheduled. Existing ciphertext recorded today by an adversary
banking on "harvest now, decrypt later" is at risk if a CRQC arrives
within the relevant time horizon.

### 3.5. Group messaging at scale

`AetherNet.Security` ships an `IGroupKeyProvider` seam, but the full
Signal Sender Keys protocol (the asynchronous group-messaging
construction Signal uses) is **not** implemented as of HEAD. Hosts
that need group messaging today fall back to N pairwise sessions —
which works but has O(N) cost per group send. PROTOCOL_SPEC §7
covers single-recipient threats only.

### 3.6. Identity verification at first contact (TOFU)

Aether authenticates that "the peer holding identity-key-X signed
this". It does **not** authenticate that "identity-key-X actually
belongs to the human Alice that the user expects to be talking to".
At first contact, an active man-in-the-middle who controls the
network during the very first bundle exchange can substitute their
own identity key, sign their own bundle, and proxy traffic in both
directions transparently.

This is the standard Signal "Trust On First Use" weakness. The
canonical mitigation is safety-number / fingerprint comparison
out-of-band (in person, via a separate channel, on a pre-shared
verification screen). The protocol does not currently expose a
public API surface for safety-number derivation; tracking it as a
gap (not yet in `OPEN_ISSUES.md`) — host UX should not pretend
verified-by-default.

### 3.7. Network-layer attacks on the underlying transport

Signal jamming (BLE, Wi-Fi, NearLink), RF-layer denial of service, and
attacks against the transport's pairing/bonding flows are out of scope.
The transport (`ITransportService`) is treated as an opaque byte pipe.
A jammer that owns the spectrum stops Aether from delivering anything.

### 3.8. Routing attacks beyond the dedup window

Sybil flooding by short-lived nodes that haven't yet accumulated a
reliability score, opportunistic relay-dropping that doesn't trigger
the reliability heuristic, and resource-exhaustion attacks that stay
under the rate limits are not specifically mitigated. The reliability
score (PROTOCOL_SPEC §3.5) deprioritises proven-bad nodes but is not
a fully-fledged Byzantine-resilient routing protocol.

---

## 4. Assumptions for security claims to hold

The defenses in Section 2 are predicated on the following invariants. If
any one of them breaks, the corresponding security property is lost.

1. **Identity-key durability.** The host stores the long-term Ed25519 +
   X25519 identity keypairs durably and securely (e.g. via
   `IPreKeyStore` against a `FileSystemKeyValueStore` wrapped in
   `EncryptedKeyValueStore`, or against the platform keystore). Loss
   of an identity key = full account compromise; the holder of the
   private key can sign anything as the original peer.

2. **CSPRNG correctness.** `RandomNumberGenerator.GetBytes` and
   `RandomNumberGenerator.GetInt32` on the target platform produce
   cryptographically secure output. The whole protocol — ephemeral
   keys, AES-GCM nonces, packet nonces, OPK ids — depends on this.
   On platforms where the BCL random source is degraded (some
   embedded targets, broken Linux entropy pools) the entire trust
   tree falls.

3. **System clock within ±5 minutes UTC.** Replay protection is
   timestamp-windowed. A device with a clock that is wildly wrong
   either rejects every packet (clock-too-old) or accepts replays
   indefinitely (clock-too-new). Hosts SHOULD ship a sanity check
   against a trusted time source on app start.

4. **Atomic OPK consumption.** When an `IPreKeyStore`-backed
   `ConsumeOneTimePreKeyAsync(id)` runs concurrently with a responder
   X3DH operation against the same id, the consume MUST succeed-or-
   fail atomically. The reference C# pool serialises consumption
   under `_preKeyLock`; a host-supplied store on a non-transactional
   backend (e.g. a naive file store with read-modify-write) may
   permit the same OPK to be consumed twice, breaking property 2.6.
   `KeyValuePreKeyStore` uses `IKeyValueStore.RemoveAsync` directly
   for consumption — atomic provided the underlying KV's remove is
   atomic.

5. **First-contact identity verification.** The peer's identity
   public key was verified out-of-band (safety number, fingerprint,
   trusted directory) before the first exchanged message — or the
   host accepts the TOFU risk and is content to detect a key change
   on next contact. Without this, §3.6 is an open MitM window.

6. **Host process memory is not adversary-readable.** §3.1.

---

## 5. Known weaknesses + mitigations

### 5.1. First-contact MitM (TOFU)

**Weakness:** an active attacker who controls the peer-to-peer link
during the very first bundle exchange can substitute their own bundle
and proxy traffic.
**Mitigation:** host UX must expose a safety-number / public-key
fingerprint comparison flow before treating a contact as verified. A
public API surface for safety-number derivation is not yet shipped in
`AetherNet.Security`; tracking as a gap.

### 5.2. Signed-pre-key rotation lag

**Weakness:** until the host calls `RotateSignedPreKeyAsync`, the
same SPK is served in every bundle. An adversary who learns the
SPK private key (e.g. via §3.1 endpoint compromise) can run X3DH
against any captured bundle dated since the last rotation.
**Mitigation:** schedule daily `RotateSignedPreKeyAsync` calls. The
default `SignedPreKeyRotationOptions` retain 3 prior SPKs so
in-flight messages signed under a recently-rotated key still
decrypt during the rotation window. The default rotation interval
is 7 days — adopters running against actively-targeted users
should shorten this.

### 5.3. In-memory session state without persistence

**Weakness:** if `SignalProtocolService` is constructed without a
`sessionStore`, a process crash or restart loses every active
session. Forward secrecy is intact (the lost keys can't be
recovered) but the next message from the peer will fail to decrypt
because the receive chain is gone.
**Mitigation:** wire `KeyValueSignalSessionStore` against a durable
`IKeyValueStore` for any production deployment. The sample console
demo uses `InMemoryDtnBundleStore` etc. for clarity; production
hosts must not.

### 5.4. Compression-flag wire byte transition window

**Weakness:** `MessagingService` has an optional Brotli-compression
seam that prepends an unconditional flag byte to the plaintext
envelope. A peer running pre-compression code will misread the flag
byte as the first byte of the application payload.
**Mitigation:** adopters set `MessagingOptions.Compression.Enabled =
false` until every peer has the new bits. The flag byte will be
gated by a future capability negotiation handshake. See the migration
note on `CompressionOptions`.

### 5.5. C-language gap

**Weakness:** the C implementation ships only the X25519 + KDF_RK
primitives plus the fixture verifier. It does **not** implement the
full `SignalProtocolService` API (X3DH session establishment, OPK /
SPK lifecycle, DH-ratchet integration). Hosts deploying Aether on
C-based microcontrollers cannot use the current C surface for
end-to-end encrypted traffic. Tracked as `OPEN_ISSUES.md` §11.

### 5.6. OPK pool is C#-only

**Weakness:** the 100-OPK pool with FIFO issue and atomic
consumption (defense 2.6) is a C# reference feature. The Go,
Python, TypeScript, Rust, Swift, Kotlin implementations still issue
a single OPK per session. Under simultaneous-initiator load, two
responders racing for the same bundle source can both observe the
same OPK and X3DH can produce a session-state mismatch.
**Mitigation:** for the affected languages, serialise bundle
consumption host-side (one initiator at a time per peer). Tracked
as `OPEN_ISSUES.md` §9.

### 5.7. Demo signing in non-C# languages

**Weakness:** the per-language demo programs (Go, Python, TS, Rust,
Swift, Kotlin, C) sign the full serialised wire bytes for
visualisation rather than the canonical `BuildSignableData` buffer.
The library code in those languages is correct — only the demos
take the shortcut, but it's confusing for porters.
**Mitigation:** tracked as `OPEN_ISSUES.md` §10. Treat the C#
demo's Step 3 as the canonical flow.

---

## 6. Reporting security issues

See [`SECURITY.md`](../SECURITY.md) for the responsible-disclosure
policy. Email `security@thegeeknetwork.co.za` with reproduction steps;
expect acknowledgement within 48 hours and an initial assessment
within 7 days.

Issues that are out of scope per Section 3 are still welcome reports —
we'd rather know what we're not defending against than have a user
discover the gap in production.
