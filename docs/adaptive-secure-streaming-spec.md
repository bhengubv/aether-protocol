# Adaptive Secure Streaming Specification

> **Status: PROPOSAL — not yet implemented.** This is a forward-design
> document. No language implementation in this repository currently provides
> the streaming, layered encryption, crypto-shredding, or data-poisoning
> behaviours described below. Treat this as a target architecture, not a
> spec for shipping code. Tracked under `OPEN_ISSUES.md` § 6.

## Purpose

Define an implementation-ready architecture for a view-once secure streaming system that combines:

- Layered encryption
- Adaptive defense
- Crypto-shredding
- Data poisoning / self-destruction
- Source-controlled delivery adaptation
- Mesh-aware secure routing

This system is optimized for:

- Secure, one-way playback
- Minimal user-visible interruption
- No rewind / no replay convenience features
- Strong resistance to spoofing, corruption, and forensic recovery
- Node-owner-first participation in a decentralized mesh network

This system is not optimized for:

- Archival access
- Rewind or replay
- Perfect content preservation
- Lowest possible latency at all times

## Core Principles

1. Playback must feel as seamless as possible under trusted conditions.
2. Security rules do not relax to preserve convenience.
3. Parent windows are the core trust boundary.
4. Child failure causes immediate shredding of the active parent window.
5. Consumed media must not remain recoverable in cache.
6. The source controls policy, adaptation, and secure routing decisions.
7. The receiver is intentionally simple and should hold minimal authority.
8. Mesh participation must favor node-owner experience and privacy.

## Substrate Dependencies

This spec is **not self-contained**. It builds on top of the Aether mesh protocol and assumes the following substrate primitives are available. Where the substrate is incomplete, an implementation of this spec inherits those gaps.

| Primitive | What this spec uses it for | Where it lives |
|---|---|---|
| **Identity keys (Ed25519)** | Marker signing, probe-reply signing, master/device-key chain, publisher_id binding | `aether-protocol` Security layer |
| **Pairwise session establishment (X3DH + Double Ratchet)** | Per-receiver session for sender-key delivery; 1:1 content key derivation | `aether-protocol` Security layer |
| **Signed packet serialization** | Open / Close / Abandon markers; child packets; sync packets | `aether-protocol` Core layer |
| **Multi-hop routing with signed RREP** | Source-to-receiver path discovery and selection; route-class scoring | `aether-protocol` Routing layer (AODV) |
| **DTN store-and-forward (72h)** | Holding parents for a receiver who is briefly offline; cross-device sync at reconnect | `aether-protocol` DTN layer |
| **Mesh peer discovery (BLE / Wi-Fi Direct / NearLink)** | Establishing the actual transport hops a route uses | `aether-protocol` Transport layer |
| **DHT publication / lookup** | Publishing device-key lists, pre-key bundle availability counts, sender-key rotation hints for Broadcast | `aether-protocol` DHT layer |
| **Reputation primitive (Qi / Karma)** | Inputs to red / grey / green route classification; publisher trust state behavioural signals | `IAetherMeshIncentiveProvider` extension seam (no-op default in OSS, real implementation in Aether by Circle) |
| **Pre-key bundle distribution** | New-pair X3DH bootstrap when the peer is not in direct mesh range | `CircleAetherMeshAPI` `/api/aether/prekey-bundles/{uhid}` |

### Implementation status notes (as of this spec revision)

These are not part of the spec itself — they are notes for implementers so they understand what the streaming layer can and cannot rely on today.

- **Ed25519, X3DH, AES-256-GCM, packet serializer:** present across all 8 language implementations of `aether-protocol`, with cross-language wire-compat issues (UUID byte order, signature endianness, ratchet construction) tracked separately. A streaming implementation must wait for those gaps to close before it can interoperate cross-language.
- **AODV routing, DTN, SOS broadcast:** referenced in the `aether-protocol` README but not implemented in any language. A streaming implementation that depends on these primitives must either ship them as part of the same effort or wait for them to land.
- **Pre-key bundle distribution endpoint:** present in `CircleAetherMeshAPI` (server + typed clients).
- **BLE / Wi-Fi Direct / NearLink transports:** in-process simulator only at the open-source layer; the private CircleAether implementation has BLE and Wi-Fi Direct platform shims pending physical-device validation.
- **Reputation primitive:** `IAetherMeshIncentiveProvider` and related extension seams exist in the open-source layer with no-op defaults. Real Qi/Karma scoring lives in the private Aether-by-Circle implementation. An open-source-only deployment of this streaming protocol gets degraded route classification (only `silent` / `forged` reputation signals — see *Probe Authentication and Asymmetric Reputation Impact*).

### What this spec adds on top of the substrate

This spec contributes the layer above the substrate:

- Parent / child window structure
- Per-parent key derivation chain (rooted in the substrate's pairwise session)
- Marker design and signature scheme
- Adaptive bitrate / probe model
- Cache encryption mechanism
- Multi-receiver topology handling (Group, Broadcast)
- Publisher trust and source revocation
- Watch-together synchronisation
- Owner emergency-shutdown path

Anything not in the list above is the substrate's responsibility.

## Glossary

- `Session Root`: Initial high-trust seed material for a stream session. It never directly decrypts media.
- `Parent Window`: Fixed logical playback-time segment. Also the unit of trust, shredding, restart, and resume.
- `Child Window`: Fixed logical subdivision of a parent window.
- `Open Marker`: Authenticated start marker for a parent window.
- `Close Marker`: Authenticated end marker for a parent window.
- `Probe`: Lightweight authenticated preflight exchange used before each parent window.
- `Qi`: Live reputation score for routing decisions. Details intentionally out of scope.
- `Karma`: Time-decayed historical trust score. Details intentionally out of scope.

## Functional Goals

- Deliver content as fixed playback-time parent windows.
- Derive related but separate keys for open, payload, close, and child payloads.
- Perform a fresh preflight probe before each parent window.
- Adapt future payload size characteristics using real observed conditions only.
- Allow clean playback jumps over compromised windows.
- Terminate and fully restart when delay or attack thresholds are exceeded.
- Support decentralized mesh routing without exposing private device state.

## Threat Model

The architecture is intended to resist:

- Man-in-the-middle spoofing
- Packet corruption
- Malicious route behavior
- Partial window tampering
- Forensic recovery from local cache
- Replay-oriented convenience abuse

The architecture assumes:

- A receiver can disappear unexpectedly without malicious intent.
- Bad network conditions are not the same as authenticated corruption.
- A mesh node can be unreliable without being hostile.
- Playback continuity is more important than preserving every exact segment.

### What this protocol cannot defend against on its own

- **Source compromise** — an attacker who controls the source's signing keys can produce valid signed packets indefinitely. Marker verification, child verification, and per-parent ratchets all succeed; the receiver has no cryptographic way to know the source is hostile. The protocol layer cannot solve this. It is mitigated above the protocol — see *Publisher Trust and Source Revocation*.
- **Out-of-band capture** — a receiver who can render content can also screen-record, camera-the-screen, or hardware-tap the display buffer. View-once is enforced cryptographically; visual-channel exfiltration defeats it by definition. This is a non-goal.
- **Hardware compromise of the receiver** — keys held in the receiver's secure enclave are by definition only as safe as the enclave. Root / jailbreak / debug-bridge attacks against the receiver are the receiver's platform problem, not the protocol's.

## Publisher Trust and Source Revocation

The protocol's per-parent crypto-shredding stops a hostile *route* from corrupting content. It cannot stop a hostile *source* from publishing legitimately signed content with malicious intent. That problem is solved one layer above the streaming protocol.

### Publisher identity

Every stream is published by a stable **publisher_id** — a long-lived identity distinct from the per-session ephemeral keys. A publisher_id is one of:

- A user's UHID (when the source is an end user, e.g. a 1:1 video call)
- A registered channel ID (when the source is a creator / broadcaster — the channel is an identity bound to a UHID and signed by it)

Each parent window's `Open Marker` carries the publisher_id, and the parent-key derivation chain is rooted in a key bound to the publisher_id. Receivers can therefore make trust decisions at the publisher level, not per-stream.

### Receiver-side publisher trust state

A receiver maintains, per publisher_id it has interacted with, one of:

| State | Meaning | Action |
|---|---|---|
| `trusted` | Default for new publishers in 1:1 calls; default after explicit subscription for broadcasters. | Accept streams; render content. |
| `suspect` | A behavioural signal has fired (see below). | Continue rendering, but show the receiver a one-line warning and elevate telemetry. |
| `revoked` | User has blocked, or an out-of-band signal (TrustSeal revocation, mesh-wide bad-actor flag) has demoted. | Reject all incoming streams from this publisher_id. Cached content from this publisher is shredded. |

State transitions are local to the receiver. The protocol does not require a global trust authority.

### Optional out-of-band reputation hooks

A receiver may, but is not required to, consult external reputation sources before accepting a new publisher_id:

- **TrustSeal notarisation** — for streams published with a TrustSeal hash, the receiver can verify the publisher_id against the on-chain attestation.
- **Mesh-wide Karma signal** — the mesh's existing publisher / node Karma score can demote a publisher_id below a configurable threshold automatically.

These hooks are optional. A pure mesh with no internet still operates at the `trusted` / `suspect` / `revoked` level using local signals only.

### Behavioural signals that move trusted → suspect

These are receiver-local heuristics; thresholds are set by the implementation:

- Sudden large rate of new streams from one publisher_id
- Authenticated child corruption from a publisher_id that previously had a clean record
- Frequent shred events on one publisher_id's streams within a rolling window
- Mismatch between published content type and the channel's declared category (only when the channel ID carries a category)

A move to `suspect` does not stop playback. It only warns the user and increases scrutiny.

### Revocation propagation

When a user moves a publisher_id to `revoked`:

- All in-flight parents from that publisher are immediately shredded.
- The local cache is purged of that publisher's content.
- Future Open Markers from that publisher_id are rejected before any key derivation.
- Optionally (and only if the user opts in), the revocation is published to the mesh as a Karma-decay signal so other nodes can use it as input.

### What this still does not solve

A first-time publisher who has not yet earned distrust can publish one malicious stream before being moved to `suspect` or `revoked`. The protocol does not prevent the *first* hostile stream from a fresh publisher_id; it ensures the *second* one can be stopped. This is an inherent trade-off of any reputation system and is called out so implementers do not assume otherwise.

## Non-Goals

- Perfect recovery of every lost frame or second
- Seamless rewind or seek
- Reuse of consumed cache
- Receiver-owned key policy
- Static predictable delivery cadence
- Artificial timing randomness

## Latency Profiles

Every stream operates under one of three latency profiles. The profile sets the parent window duration, child count, and probe behaviour — these are not global constants. The profile is fixed at session start and does not change mid-session.

| Profile | Use case | Parent window | Children per parent | Probe |
|---|---|---|---|---|
| **A — Real-Time Call** | Voice and video calls | 100–150 ms | 3 | Skipped — replaced by a rolling RTT / loss estimate maintained continuously from delivered children. A discrete probe round-trip exceeds the budget. |
| **B — Live Broadcast** | Live streaming, events, concerts | 500 ms – 1 s | 3 – 5 | Lightweight, one round-trip, before each parent. |
| **C — VOD** | Pre-recorded and on-demand content | 5 – 10 s | 5 – 10 | Full probe + alternate-route check before each parent. |

The profile constant resolves the following items per stream:

- Parent playback duration → from the profile's range
- Child count per parent → from the profile's range
- Probe model → skipped, lightweight, or full
- Adaptive-bitrate cadence → per-child for A, per-parent for B and C

For Profile A, the rolling estimate replaces the probe. The estimate updates from each delivered child's measured arrival time and validation result; the source applies the same accept-or-shred logic but does not gate the next parent on a discrete probe response.

### Bitrate Ladder (default)

Each profile ships with a default bitrate ladder. Implementations may extend or replace the ladder; the values below are protocol-level defaults so that two independent implementations of the same profile interoperate at known operating points.

| Profile | Audio (Opus) | Video (H.265 / VP9) |
|---|---|---|
| **A — Real-Time Call** | 16 / 32 / 64 kbps | 144p @ 200 kbps · 240p @ 400 kbps · 360p @ 800 kbps |
| **B — Live Broadcast** | 64 / 96 / 128 kbps | 360p @ 800 kbps · 480p @ 1.5 Mbps · 720p @ 3 Mbps · 1080p @ 5 Mbps |
| **C — VOD** | 96 / 128 / 192 kbps | 480p @ 1 Mbps · 720p @ 2.5 Mbps · 1080p @ 5 Mbps · 1440p @ 9 Mbps · 2160p @ 16 Mbps |

Adaptation cadence:

- **Profile A:** per-child. Each child is encoded at a rung chosen from the rolling estimate. Codec-level fast-switch (Opus mode change, H.265 SPS update at IDR boundary).
- **Profile B and C:** per-parent. The probe sets the rung for the entire parent. Switches happen only at parent boundaries.

The lowest rung in each ladder is the **floor** — when probe / rolling estimate suggests the link cannot sustain even the floor, the source emits an `AbandonMarker` rather than a degraded parent. Quality below the floor is not delivered.

## Stream Topology

Every stream operates under one of three topologies. The topology is orthogonal to the latency profile and the mode — it is fixed at session start and does not change mid-session.

| Topology | Definition | Typical use |
|---|---|---|
| **1:1** | One source, one receiver. Direct pairwise session. | Voice / video calls between two people. |
| **Group** | One source, **N receivers** where N is small (≤ 32 by default; implementations may set their own ceiling). All receivers reachable via mesh peer-to-peer or short relay chains. | Group calls, watch-together rooms, small live audiences. |
| **Broadcast** | One source, **many receivers** (no fixed upper bound). Receivers reached via SFU relay, hierarchical mesh fan-out, or BitTorrent-style content distribution. | Live events, concerts, public broadcasts, VOD with many viewers. |

The total first-class combination space is `Topology × Profile × Mode = 3 × 3 × 2 = 18`. Implementations are not required to support every combination, but the spec defines behaviour for all of them so a future implementation has unambiguous semantics. In practice, common shipping combinations are:

- **1:1 + A + secure** — a private encrypted call
- **Group + A + secure** — a group call
- **Group + C + secure** — watch-together
- **Broadcast + B + secure** — encrypted live event
- **Broadcast + C + non-secure** — public VOD
- **1:1 + B + secure** — a one-on-one live performance / lesson

## Secure and Non-Secure Modes

A stream runs in one of two modes. The mode is orthogonal to the latency profile and the topology — every profile and every topology supports both modes — and is fixed at session start.

| Concern | Secure mode | Non-secure mode |
|---|---|---|
| Per-parent key derivation | Session Root → Parent Key N → `K_open` / `K_payload_root` / `K_close` + per-child keys | Single stream key for the whole session; no per-parent derivation |
| Child-failure handling | Shred the active parent, deny playback from it, jump to next verified parent | Skip the failed child or parent, continue without shred |
| Cache | Re-encrypt consumed cache; view-once; no rewind | Plain cache allowed; rewind, seek, and replay permitted |
| Route eligibility | Green nodes preferred, grey avoided when a better path exists, **red excluded** | All route classes allowed (red, grey, green) |
| Probe-failure response | Delay the next parent or terminate the session per the delay budget | Log the failure and continue |
| Re-keying after shred | New session material bound to destination device and biometrics | Not applicable — no shred occurs |
| Telemetry | Coarse counters only (success/failure, no content correlatable to a user) | Standard delivery telemetry |

Six combinations are first-class: `{A, B, C} × {secure, non-secure}`. Implementations must handle all six. The mode applies uniformly across the session — a stream cannot upgrade from non-secure to secure mid-session, nor downgrade.

## High-Level Architecture

### 1. Session Structure

Each session uses a rolling ratchet structure:

- `Session Root`
- `Parent Key N`
- `Parent Key N+1`
- `Parent Key N+2`

The ratchet advances per parent window. Parent windows are isolated so a failure in one parent does not directly destroy future parents.

### 2. Parent Window Structure

Each parent window has:

- Fixed logical playback duration
- Fixed child count
- Authenticated `Open Marker`
- Authenticated payload envelope
- Authenticated `Close Marker`

Each parent derives:

- `K_open`
- `K_payload_root`
- `K_close`

Each child derives its own key from `K_payload_root`:

- `K_child_1`
- `K_child_2`
- `K_child_3`
- `K_child_4`
- `K_child_5`

The exact child count is fixed by policy. The conversation settled on a stable fixed child count per parent.

### 3. Receiver Model

The receiver is a dumb receiver with a key, by design:

- It does not choose policy.
- It does not choose window cadence.
- It does not predict future window shape.
- It verifies markers and payload.
- It decrypts only what is needed for immediate playback.
- It re-encrypts consumed cache to deny later forensic recovery.

## Windowing Model

### Parent Windows

- Parent windows map to fixed playback timeline intervals.
- Every parent window is a valid restart point.
- Parent windows are the only allowed restart point.
- No mid-parent resume is allowed.

### Child Windows

- Child windows also map to fixed playback timeline slices.
- Child count remains fixed across parents.
- Child timeline meaning never changes due to network conditions.

### Adaptation Rules

Network conditions may change:

- Bitrate
- Compression level
- Payload density for future parent windows
- Probe and route-selection behavior

Network conditions may not change:

- Parent timeline duration
- Child count
- Child timeline structure
- Shred policy
- Trust boundaries

## Probe / Preflight Model

Before each parent window, the source performs a lightweight authenticated probe.

### Probe Inputs

The probe determines:

- Usable throughput
- Packet loss
- Verification time
- Route availability

No artificial jitter or synthetic randomness is added. Only real network conditions alter timing and size behavior.

### Probe Requirements

- Probe happens before every parent window.
- Probe is tied to the current parent-key chain.
- The next parent window cannot be derived and sent until the probe succeeds.
- If the probe fails, alternate secure routes may be tested within a bounded retry budget.

### Probe Authentication and Asymmetric Reputation Impact

A naive design where any received probe reply influences route reputation is exploitable. An attacker on the mesh can spoof replies that appear to come from honest nodes and trigger their demotion. The protocol therefore requires authenticated probe replies and applies reputation changes asymmetrically.

**Authentication.** Every probe reply must be signed by the responder's identity key (the same key bound to the responder's UHID and publisher_id). The source verifies the signature before doing anything else with the reply. An unsigned reply, or one whose signature does not verify against the apparent responder's known identity key, is **discarded silently** — it does not influence the responder's reputation.

**Asymmetric reputation impact** by reply class:

| Reply class | What it indicates | Reputation impact on the responder |
|---|---|---|
| Authentic, success | Route is healthy from the responder's vantage. | Small positive (`green-tendency`). |
| Authentic, reports congestion / loss / latency | Route is degraded but the responder is honest about it. | Small negative (`grey-tendency`) — the responder is **not** treated as malicious. |
| Authentic, contradicts ground truth (e.g. signed "all good" while children fail to arrive) | The responder is signing false claims. | Larger negative (`red-tendency`). |
| Silent (no reply within budget) | Could be link loss, could be an offline node, could be deliberate. | Very small negative (`uncertain`) — applied per-occurrence with low weight, with rate-limiting per peer per hour to prevent observation-amplification attacks. |
| Apparent reply with invalid signature | An attacker is spoofing this responder's identity. | **No impact on the apparent responder.** Discarded. The attempt itself is logged and contributes to a separate `mesh_noise` counter. |
| Apparent reply with no signature at all | Same as above. | No impact on the apparent responder. |

**Forged-reply forensics.** When a source receives invalid-signature probe replies repeatedly, it can usually identify the upstream link that delivered them. A peer that consistently delivers forged probe replies for *other* nodes is itself behaving as a man-in-the-middle. The source applies a reputation hit to that delivering peer (not to the apparent responder) once a threshold is exceeded — typically three forged deliveries from the same peer within one minute.

**Probe reply structure.** A probe reply carries:

- Responder UHID
- Probe nonce (echoes the source's challenge)
- Timestamp
- Health fields (RTT, loss, advertised bandwidth)
- Responder's signature over the four fields above using the responder's identity key

The source's verification path is signature → nonce match → timestamp freshness window (5 minutes) → only then do the health fields influence reputation.

**What this still doesn't solve.**

A compromised responder that holds the identity key can sign true or false replies at will. The protocol cannot distinguish a captured responder from a hostile one. The publisher trust system handles this above the protocol — see the *Publisher Trust and Source Revocation* section. Probe-level reputation is bounded; capture-level distrust is escalated to the publisher trust state machine.

### Retry Budget

Retry budget is bounded by:

- Maximum alternate route count
- Maximum probe time budget

Both limits must be enforced.

If no secure route succeeds within budget:

- The next parent window is delayed.
- The current parent, if any, completes normally.
- No emergency reserve window is used.

## Playback Rules

### Playback Start

Playback for a parent window starts only after:

- Full parent window receipt
- Successful marker verification
- Successful payload verification
- Successful parent acceptance

No asynchronous partial trust is permitted.

### Playback Flow

- Playback is one-way.
- No rewind.
- No replay convenience.
- No persistent usable cache.
- Consumed media is re-encrypted in cache.

### Backward Seeking

- Disallowed.
- The system behaves as a view-once stream.

## Failure and Shredding Rules

### Immediate Child Failure Response

If any child:

- Fails validation
- Decrypts unexpectedly
- Shows authenticated corruption

Then:

- Shred the current parent immediately.
- Deny further playback from that parent.
- Invalidate the parent only.
- Do not poison future parents automatically.

### Rekey After Shred

After a shred event:

- Trigger a new background rekey.
- Bind acceptance of new session material to the destination device and biometrics.
- Keep the receiver lightweight.
- Keep source authority over restart and session continuity.

### Biometric Binding Mechanism (default)

Re-keying after shred binds new session material to the destination device + biometrics. Concrete defaults:

| Platform | Biometric API | Key store | Algorithm |
|---|---|---|---|
| **iOS / iPadOS / macOS** | LocalAuthentication (`LAContext.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics)`) | Keychain Services with `.biometryCurrentSet` access control | ECDSA P-256 in Secure Enclave (key non-exportable) |
| **Android** | BiometricPrompt | Android Keystore with `setUserAuthenticationRequired(true)` | ECDSA P-256 in StrongBox / TEE (key non-exportable) |
| **Windows** | Windows Hello (`KeyCredentialManager`) | Microsoft Passport credential | RSA-2048 or ECDSA P-256, hardware-backed when TPM available |
| **Web (Aether-by-Circle Browser variant)** | WebAuthn Platform Authenticator | Browser-managed credential store | ECDSA P-256 (per WebAuthn defaults) |

The streaming layer's identity keys remain Ed25519 (matching the rest of the Aether protocol). The biometric-bound key is a **wrapping key** that encrypts the device-key chain at rest. The Ed25519 identity key is unwrapped after biometric-success, used for signing, and zeroized when the session ends or when the biometric grace window expires (default 5 minutes).

**Fallback when biometric is unavailable** (no enrolled fingerprint, sensor failure, accessibility need):

- Device PIN of at least 6 digits is accepted as a fallback.
- After three consecutive PIN failures the device-key chain is locked for 60 seconds; after ten, it requires master-key reauthentication (recovery seed input).
- The user may opt **out** of PIN fallback at account setup. In that case a missing biometric simply blocks the rekey — the user reauthenticates on a different device or via the recovery seed.

Biometric-bound material is never transmitted off-device. The wrapping key never leaves the secure enclave / Keystore / TEE / TPM. Only the unwrapped ephemeral session keys touch the streaming pipeline, and they are zeroized per the existing rules.

### Multi-Device and Device Replacement

Per-device key binding works inside a single device's lifetime. A real user changes phones, replaces a lost device, runs the same identity on a phone *and* a tablet, etc. The protocol treats this as the normal case rather than the exception.

**UHID is identity, device-keys are bindings.** A UHID is the user-level pseudonym. A UHID can have **N active device-keys** at any moment (default cap N=4; implementations may raise it). Each device-key is a Signal-style address — its own X3DH identity key, its own pre-key bundle, its own pairwise sessions.

**Source-side addressing.** When a source streams to a UHID, it expands the UHID to the current set of active device-keys via a published *device-key list*. The source establishes a pairwise session with each active device-key and delivers the sender key (Group / Broadcast) or the per-pair X3DH session (1:1) to each. Each of the user's devices receives independently.

**Device-key list publication.** A UHID's device-key list is signed by the UHID's *master key* — a long-lived key held only by the user, not on any single device, established at first account setup and backed up out-of-band (e.g. a recovery seed, a hardware key, or the BIP39 backup mechanism described in the surrounding ecosystem). Each entry in the list is:

- Device-key public identity
- Device label (optional, for the user's UI; e.g. "iPhone 15", "Lenovo laptop")
- Date added
- Signature by the master key

The list is published to the mesh DHT and to `CircleAetherMeshAPI` (when reachable). Sources fetch the latest list before establishing new sessions.

**Adding a new device.**

1. New device generates its own X3DH identity key and pre-key bundle locally.
2. User authenticates on the new device using the user-level auth (phone + OTP + biometric, per the surrounding ecosystem's auth standard).
3. The new device proves possession of the master key — either via direct user input (recovery seed) or via an existing trusted device co-signing the addition (out-of-band confirm tap).
4. The master key signs the updated device-key list with the new device included.
5. The updated list is published. Sources will fetch it on their next session establishment with this UHID.

In-flight streams to the *other* devices of this UHID continue uninterrupted. The new device starts receiving from the next stream the source sends — it does not retroactively decrypt content that was already streamed before its device-key was added.

**Removing a device (loss, theft, replacement).**

1. User invokes "remove device" on any *other* device tied to the same UHID, or via a recovery flow (recovery seed input on a fresh device).
2. The master key signs an updated device-key list with the lost device removed.
3. The list is published.
4. Sources fetch the new list on their next session-establishment cycle and stop including the removed device-key in addressing.
5. **In-flight Group / Broadcast streams immediately rotate the sender key** at the next parent boundary. The removed device is no longer in the recipient set for the rotated key. Any session keys the removed device still has expire as the parent ratchet advances.
6. **In-flight 1:1 streams to the removed device are terminated.** The source emits an `AbandonMarker` with `reason=peer_revoked` and the per-pair session is destroyed.

The user does not need to re-authenticate every contact. Trust state on contacts is held at the UHID level; the device-key swap is invisible at that layer.

**No master key on a single device.** The master key is the recovery anchor. It is **never** stored as plaintext on any device. A device that wants to perform a device-list update must reconstruct the master key transiently — either by user input of the recovery seed (manual flow) or by collecting co-signatures from other already-trusted devices (multi-device flow). This makes a stolen device unable to add or remove devices on the user's behalf.

**Forward secrecy across device replacement.** A device replacement does **not** unlock historical content for the new device. Pre-replacement content was encrypted under the old device's keys and was already shredded under the per-parent CEK rules. The new device starts fresh.

**No re-authentication of contacts is required.** The UHID is unchanged. Existing trust relationships persist. The contact's source code addresses the UHID, not the device-key — the device-key list expansion is automatic.

### Session Continuity After Shred

After a shred event:

- Do not stall to recover the compromised parent.
- Jump cleanly to the next verified parent window.
- Show no warning to the user.
- Preserve session continuity over exact content continuity.

## Delay, Restart, and Termination

### Adaptive Delay Budget

The source determines an adaptive maximum delay using:

- Throughput
- Loss
- Verification time
- Route availability

If the maximum delay is exceeded:

- End the session
- Discard the current chain
- Require a fresh full restart with new session material

### Restart Behavior

On restart:

- Resume only at a parent-window boundary
- Resume from the nearest future safe point
- Never resume inside an old parent
- Never restart from the beginning by default

### Mid-Parent Bandwidth Collapse

The probe (or rolling estimate) measured conditions at the start of the parent. The link can collapse after the parent has begun streaming. The protocol handles this without converting it into a session-level failure.

**Source-side detection.** The source watches transport-level send-success at child granularity. Triggers for "mid-parent collapse":

- Three consecutive children fail to acknowledge or are reported lost by the transport (Profiles B and C — children are large enough that loss is observable)
- Rolling RTT exceeds the parent's remaining time budget (Profile A — there is no per-child ack, but the rolling estimate flips fast)
- Transport reports `BackpressureFull` and the source's outbound child queue cannot drain within the remaining parent budget

**Source-side response.** The source emits an `AbandonMarker` instead of the next child. The AbandonMarker is signed by the source's identity key the same way Open and Close are. It carries:

- `parentIndex` — which parent is being abandoned
- `childrenSentCount` — how many children were emitted before abandonment
- `reason` — `congestion` / `route_failure` / `internal_error` (informational only; not security-critical)
- `recommendedBitrateClass` — a hint for the next parent's encoding (`drop_one_class`, `drop_two_classes`, `pause`)

After emitting the AbandonMarker the source treats the parent as ended and proceeds to the next parent's probe / build cycle. The next parent uses the recommended bitrate class as a starting point.

**Receiver-side response.** Receipt of an AbandonMarker for the current parent triggers `ParentShred`:

- Discard all received children for the parent
- Destroy the parent's CEK (consumed cache becomes unrecoverable)
- Emit no playback for the abandoned parent — the user sees a clean jump to the next parent's content with no error message (per the existing failure-and-shredding rules)
- Update the receiver's rolling estimate of source health for adaptive UI hints (e.g. a low-priority indicator that quality may be reduced for a few parents)

**Repeated abandonment policy.** If three consecutive parents are abandoned, the source treats this as a sustained-collapse condition and either:

- Falls back to `recommendedBitrateClass=pause` and stops emitting parents until the probe succeeds at any bitrate, **or**
- Terminates the session per the existing Adaptive Delay Budget rules

Sustained collapse is **not** a security event. It does not affect publisher trust state. It is a network condition.

**Profile A specifics.** Real-time call abandonment is silent — no AbandonMarker is sent (there is no time to). The source simply stops emitting children. The receiver's rolling estimate detects the gap and shreds the parent on its own. The Profile A AbandonMarker is implicit: silence for one full parent duration.

## Repeated Failure Policy

If the attack or corruption pattern repeats three times within a recent rolling window:

- Terminate the session
- Restart with fresh session material

The rolling threshold is adaptive and should not be a whole-session lifetime counter.

### Rolling Failure Threshold Window (default)

| Profile | Window duration | Approximate parent count in window |
|---|---|---|
| **A — Real-Time Call** | 30 seconds | 200–300 parents |
| **B — Live Broadcast** | 5 minutes | 300–600 parents |
| **C — VOD** | 15 minutes | 90–180 parents |

Three failures within the window of the active profile triggers session termination. Fewer than three, or three spread across more than the window, is treated as ordinary network noise.

A **failure** here is one of: child verification fail, marker signature fail, abandonment due to congestion, or probe failure with all alternate routes exhausted. Ordinary packet loss without verification failure does not count.

## Marker Design

Each parent window uses authenticated:

- `Open Marker`
- `Close Marker`

Rules:

- `Open Marker` is sent first
- `Close Marker` is sent last
- Receiver knows when the parent starts and stops
- Markers are cryptographically associated with the parent via related key derivation

Markers should be verified independently from payload using:

- `K_open`
- `K_close`

Payload and children use:

- `K_payload_root`
- Per-child derived keys

## Key Hierarchy

Recommended key derivation shape:

1. `Session Root`
2. Rolling parent ratchet derives `Parent Key N`
3. `Parent Key N` derives:
   - `K_open`
   - `K_payload_root`
   - `K_close`
4. `K_payload_root` derives each child key

Requirements:

- Parent windows are isolated
- Child keys are distinct
- Markers are distinct from payload keys
- Shredding one parent does not kill the full session by default

## Multi-Receiver Topologies

The Key Hierarchy above describes key derivation for **one** source-receiver pair. Group and Broadcast topologies extend it without replacing it.

### Group topology — key model (Sender Keys)

A group of N receivers does not run N independent pairwise sessions for content payload. Doing so would require the source to encrypt every parent N times. Instead:

1. The source has a **sender key chain** rooted in its publisher_id. The sender key advances per parent window — the same parent ratchet defined in the Key Hierarchy section, but rooted at the sender, not at a pairwise session.
2. Each parent's `K_payload_root` and child keys are derived from the sender's parent key. The source encrypts each child once and emits one packet per child to the mesh.
3. Each receiver runs a **pairwise X3DH session with the source** (the existing 1:1 mechanism) used only to receive the sender key — not the content. The sender key is delivered out-of-band via the pairwise session at session start and again on every key rotation.
4. `K_open` and `K_close` are still per-parent and signed by the source's identity key. Receivers verify them using the source's public identity key (already known via publisher_id binding).

Forward secrecy: the sender key advances per parent. Each parent's key cannot derive the next parent's key without the source's ratchet state. A compromised receiver only learns content from parents whose sender key it actually held.

### Broadcast topology — key model

Identical to Group, with one constraint: there is no reliable return channel from individual receivers back to the source. Joins and leaves are not signalled in real time. As a result:

- Sender key rotation is **scheduled**, not event-driven. A new sender key is published every K parents (K is implementation-defined; default K = 60 — i.e. once per minute at Profile B 1-second parents).
- Receivers fetch the latest sender key from any of: the source's pairwise session at first contact, the SFU relay's published-key endpoint (with the public-key signed by the source), or the mesh DHT.
- A receiver who joins mid-stream gets the **current** sender key — not historical ones. Earlier parents are not retroactively decryptable.

### Routing per topology

| Topology | Path | Notes |
|---|---|---|
| **1:1** | Direct mesh peer-to-peer (BLE / Wi-Fi Direct / NearLink), gateway-relayed when out of physical range. | Lowest latency. Profile A typical. |
| **Group** | Source → N peer-to-peer fan-out where mesh range allows. Mesh hops permitted. Falls back to a shared relay (e.g. an SFU at a gateway node) when fan-out exceeds practical mesh range. | Profile A and Profile C typical. |
| **Broadcast** | Source → SFU relay (or hierarchical mesh fan-out tree, or BitTorrent-style chunk distribution for VOD). Direct peer-to-peer not used. | Profile B and Profile C typical. |

Secure mode constraints (red-excluded, green-preferred) apply at **every hop** of the path, not just the source-to-first-relay leg. An SFU that wants to carry secure traffic must itself be a green node.

### Group membership and dynamics

**Join.** When a new receiver wants to join a Group or Broadcast stream:

1. Receiver establishes a pairwise X3DH session with the source (or, for Broadcast, fetches the sender's published pre-key bundle and runs X3DH).
2. Source delivers the **current** sender key — not historical ones.
3. Receiver begins consuming from the next parent boundary.

**Leave.** When a receiver leaves a Group stream (intentional disconnect or kick):

1. The sender's next sender-key rotation excludes the leaver.
2. For Group topology, the source rotates the sender key **at the next parent boundary** so the leaver cannot decrypt subsequent parents.
3. For Broadcast, the rotation happens on the next scheduled rotation tick (not immediately) — leavers can decrypt at most K-1 more parents. Implementations that need stricter semantics can shorten K or mark the leaver `revoked` to fast-rotate.

**Kick by source.** When the source explicitly removes a receiver:

1. Source moves the receiver's publisher_id-pair-session to `revoked` locally.
2. Sender key is rotated immediately at the next parent boundary (Group) or immediately (Broadcast — rotation is forced).
3. The kicked receiver receives no further sender keys.

### Watch-together synchronisation

Watch-together is **Group topology + Profile C + secure mode** with one additional concept: synchronised playback timeline.

- The source publishes a `WatchSyncMarker` alongside each parent's `Open Marker`. The sync marker carries: `playbackPositionMs`, `expectedClockMs`, `playState (playing/paused/seeking)`.
- Receivers maintain a clock-skew estimate against the source (RTT-based) and present content at the source's intended position ± skew.
- Seek (source-initiated): the source emits a `WatchSyncMarker` with the new `playbackPositionMs` and a `playState=seeking` hint. Receivers shred all in-flight parents whose payload no longer matches the new position and resume from the next valid parent at the new position.
- Receivers cannot seek independently of the source. The protocol enforces synchronisation; UI may offer a "leave the room" path that converts the receiver to a private 1:1 stream of the same content (out of scope for the protocol — orchestrated above).

## Routing Model

### Secure Routing

Secure traffic must use only secure-capable routes.

Red nodes:

- Must not carry secure traffic
- May carry regular traffic by separate network policy

Grey nodes:

- May remain in the network
- Should be avoided for secure routing when better paths exist

Green nodes:

- Preferred for secure traffic

### Route Selection

Secure route selection should favor:

- Stable paths
- Better reputation
- Delivery success
- Lower corruption risk

Even if the path is slightly longer.

### Route-Class Thresholds (default)

A node's per-hop route class is computed from its `Karma` score over a rolling 24-hour window. Implementations may tune the boundaries; the defaults below are protocol-level so independent implementations agree on classification given the same Karma.

| Class | Karma range (rolling 24h) | Abuse flags | Used for secure traffic? | Used for non-secure traffic? |
|---|---|---|---|---|
| **Green** | ≥ 100 | None | Preferred | Preferred |
| **Grey** | 0 – 99 | At most one minor flag (e.g. one missed delivery, one slow probe response) | Avoided when a green path exists; allowed only as a fallback inside the alternate-route retry budget | Allowed |
| **Red** | < 0, or Karma 0 with one or more major flags (authenticated child corruption, repeated forged-probe delivery, mesh-wide bad-actor designation) | Any major flag | **Excluded** | Allowed at user discretion (a non-secure stream may still want to deprioritise red nodes; the protocol does not force this) |

Karma earning rates that affect class membership:

- Successful delivery of a parent (Open + all children + Close all forwarded) → +1 Karma
- Successful probe response → +0 (probe responses are necessary baseline; not rewarded)
- Authentic-but-degraded probe response (honest report of poor link) → +0 (not penalised)
- Authenticated child corruption from this node → −10 Karma + one major flag
- Forged-probe delivery (≥ 3 in one minute) → −5 Karma + one major flag per occurrence
- Silent / no-response (per occurrence) → −0.1 Karma, capped at −5 / hour to prevent observation-amplification

A node's class can rise when its bad behaviour ages out of the 24-hour window. There is no permanent ban at the protocol level; permanent revocation is held at the publisher trust layer (which is per-receiver, not protocol-wide).

### Alternate Route Probing

If the primary secure route fails probe:

- Try a very small number of alternate secure routes
- Respect route-count and time budgets
- Delay if none pass quickly enough

## Mesh Node Reputation

Detailed `Qi` and `Karma` design is intentionally left to the author.

The implementation must support:

- Per-hop scoring
- 24-hour overall perspective
- Context-sensitive quality differences
- Time-decayed recency
- Distinguishing network weakness from authenticated corruption

Minimum reputation event categories:

- Delivery success -> green tendency
- Packet loss / disappearance -> grey tendency
- Authenticated corruption / unexpected action -> red tendency

Reputation sharing rules:

- Shared only on request
- Signed by the answering node
- Treated as aggregated input, not blind truth
- Old data fades

## Node Owner Experience and Privacy

This is a hard priority.

### Owner Experience Rules

- Defaults favor node-owner experience
- Contribution is background-only by default
- Device impact must shrink before the owner notices
- Relay load scales down before whole features turn off
- Local controller can reduce contribution based on:
  - battery
  - thermal state
  - bandwidth
  - active use

### Privacy Rules

- No habit-learning
- No sharing private device reasons with the mesh
- Mesh sees only:
  - `available`
  - `limited`
  - `unavailable`

### Side-Channel Analysis of the Coarse Availability State

The three-value availability state is deliberately coarse. Even so, a stream of `available / limited / unavailable` transitions emitted over time is itself a signal. The protocol must constrain what that signal can reveal.

**Identified leakage vectors.**

1. **Pattern fingerprinting.** A user's sequence of state changes over hours or days is a fingerprint. Sleep, work, commute and meal patterns all show. Two users with similar patterns can be linked.
2. **Co-presence inference.** If two nodes flip `available → limited` at the same minute every day, an observer can infer co-location (same room, same building) without either node revealing its location.
3. **Local-area correlation.** Nodes in the same physical area share environmental causes — weather, power outages, network congestion. Coordinated state changes leak coarse geography.
4. **Battery-state correlation.** A node going `limited` is often a low-battery signal. Many nodes in the same area going `limited` simultaneously suggests shared electrical infrastructure failure or shared usage event.
5. **Trailing-edge leakage.** A node that announces `going unavailable` is broadcasting that it is about to disconnect — useful to an attacker timing a session-hijack or impersonation attempt.

**Required mitigations.** All implementations must:

- **Quantise the broadcast cadence.** Availability transitions are not emitted on the underlying state change. They are emitted on a fixed schedule — once per 30-second bucket — with the bucket's dominant state. Sub-bucket noise is hidden.
- **Hide trailing edges.** A node going `unavailable` does not emit a final state-change packet. It simply stops responding. Other nodes infer unavailability from absence on their own keepalive checks. This is symmetric across all three transitions out of `available` — the spec defines one-way visibility for the *availability axis*.
- **Limit visibility scope.** Only nodes that have an established session-pair (mesh handshake completed in the last 30 days) see another node's availability. New nodes see no availability state at all.
- **Reject availability subscription requests.** A node may not request another node's availability stream. Availability is broadcast to known peers only.

**Optional mitigations.** Implementations targeting higher threat models may add:

- **Cover transitions.** Random `available → limited → available` flips inserted at low frequency to dilute pattern-fingerprinting. Cover-transition rate is implementation-specific.
- **Per-peer fuzzing.** Each peer sees the same node's availability bucket with a small random offset (e.g. ±5 seconds), so pairwise correlations across observers do not align.
- **Disable availability broadcast entirely.** A `mesh-silent` user mode hides availability from all peers. The user is reachable via direct connection requests only — every connection requires explicit accept.

**What is still leakable after these mitigations.**

The bucket-level pattern is still observable to a node's own peer set. A peer who has been a contact for years still sees enough of the user's coarse rhythm to make some inferences. The protocol does not pretend otherwise. Users who require stronger metadata privacy than the contact-graph allows should use `mesh-silent` mode.

### Owner Controls

Owners can override automatic behavior with simple modes such as:

- more private
- balanced
- more helpful

The mesh still only sees coarse availability states.

### Participation Changes

If the owner changes mode:

- The change is queued locally
- It takes effect after the authenticated `Close Marker` of the current parent window is processed
- Emergency local shutdown remains possible at any time — see *Emergency Shutdown* below

### Emergency Shutdown

The mode-change queue policy waits for the next parent-close boundary. That is acceptable for `more private` ↔ `balanced` ↔ `more helpful` toggles which are not safety-critical. It is **not** acceptable for cases where the user needs the stream off immediately (panic, screen-grab in progress, "I just walked into the wrong room").

The protocol therefore exposes a separate path that does not wait for any boundary.

**Triggers.** Any of:

- User explicit gesture (a UI affordance — implementation-specific, but must be reachable in one tap from the streaming surface)
- System safety event (a Panik / SOS event from the broader Aether ecosystem, when the user has opted that into stream control)
- Forced lock (device hardware lock with `secure-stream-on-lock` enabled in user preferences)

**Action sequence on trigger** (executed in order, no waits between steps):

1. **Stop the transport.** Outbound and inbound packet handlers are detached from the network. No further children are emitted (source) or accepted (receiver).
2. **Zeroize all in-RAM keys.** Current parent CEK, current sender key (or pairwise session key), parent-key chain state, rolling estimate state — all overwritten with zeros and freed.
3. **Purge cache.** All cached parent content (across every parent of the active session) is invalidated. Cache pages are marked for OS reclamation. Optional forensic-grade scrub runs if enabled.
4. **Best-effort upstream signal.** A transport-level disconnect packet is fire-and-forgotten upstream. The source (or relay) will notice and treat it as a peer loss. The shutdown does not wait for or depend on this packet's delivery.
5. **Mark session ended.** Local session state is set to `ended_by_user_emergency`. This is a distinct end state from `ended_normally` — it informs telemetry and any post-session UI but is not visible on the wire.

**Source-initiated emergency shutdown.** When the *source* triggers an emergency shutdown (e.g. broadcaster pressing panic):

- Source emits one final `AbandonMarker` with `reason=source_emergency` (best-effort, single packet — does not wait for ack)
- Source then runs the same five-step sequence
- Receivers seeing the AbandonMarker shred the parent. Receivers that miss the marker hit a transport disconnect and reach the same end state via timeout

**State preserved across emergency shutdown.** The next session may legitimately resume with the same parties.

- Publisher trust state (`trusted` / `suspect` / `revoked`) is unchanged. An emergency shutdown is not an accusation.
- User mode preferences are unchanged.
- Pre-existing X3DH session material with peers is destroyed (the in-RAM zeroize covers it). New sessions start with a fresh X3DH from published pre-key bundles.

**State NOT preserved.**

- Current parent's content — shredded.
- Sender key (for source) — destroyed. The next session re-derives.
- Rolling RTT/loss estimate — discarded. The next session probes from cold.

**Profile and topology specifics.**

- **1:1.** Emergency shutdown is unilateral. The other peer notices via transport disconnect within one keepalive cycle.
- **Group.** Emergency shutdown of one receiver removes that receiver from the source's active-receiver set on the next sender-key rotation. Other receivers continue uninterrupted.
- **Broadcast.** Emergency shutdown of one receiver is invisible to the source. The SFU drops the connection on its next keepalive check.
- **Source side.** Source emergency shutdown ends the stream for everyone. There is no recovery within the same session — receivers see the AbandonMarker (or a transport disconnect) and the session is over.

The emergency-shutdown path is **always available**, even in non-secure mode. The cache-purge and key-zeroize steps still run; they protect the receiver even when the transport was unencrypted.

## Receiver Cache Rules

- Decrypt only long enough for consumption
- Re-encrypt consumed cache immediately after use
- Do not preserve replay-friendly cache
- Do not allow rewind from local cache

### Cache encryption mechanism

The receiver maintains a **Cache Encryption Key (CEK)** distinct from any key in the source-side hierarchy. The CEK exists only in volatile memory; it is never written to persistent storage.

- **Algorithm.** AES-256-GCM. Same primitive used elsewhere in the Aether protocol; no new cipher.
- **Key derivation.** A new CEK is generated by the receiver at the start of each parent window using a CSPRNG. The previous parent's CEK is destroyed (zeroized in RAM) once that parent has finished playback **and** its consumed bytes have been re-encrypted under the new CEK or discarded.
- **Per-parent rotation.** One CEK per parent window. A captured CEK only exposes the content of one parent — at most a few seconds (Profile A or B) or a few tens of seconds (Profile C).
- **Re-encryption flow.** As children are consumed:
  1. Decrypt one child's worth of payload using the source-side derived child key.
  2. Render to the playback pipeline.
  3. Encrypt the consumed bytes under the current parent's CEK and write to cache.
  4. Discard the plaintext immediately.
- **Cache layout.** Cached bytes are tagged with `(parentIndex, childIndex, parentCEK_id)`. The parentCEK_id is a non-secret identifier so the cache layer knows which parent's bytes to evict when that parent's CEK is destroyed.
- **Cache eviction.** Triggers:
  - Session end → all CEKs destroyed → entire cache becomes unrecoverable. The cache pages themselves are also marked for OS-level reclamation.
  - User-initiated clear → same as session end.
  - OS memory pressure → CEKs may be evicted under pressure; their associated cache pages are unrecoverable from that moment.
  - Parent shred (child-fail or marker-fail) → that parent's CEK is destroyed immediately; its cached children become unrecoverable. Other parents are not affected.

### Implementation choice — per-stream vs per-parent CEK

Per-parent CEK is the default and is required for **secure mode**. For **non-secure mode** an implementation may use a single per-stream CEK to reduce CSPRNG and key-management overhead — the security guarantee is weaker but the mode does not promise crypto-shredding.

Profile A (real-time call) on resource-constrained receivers may downgrade to per-stream CEK in non-secure mode only. Secure-mode Profile A still rotates per-parent — the parent windows are short (100-150 ms) but key rotation is a few-microsecond operation on modern CPUs and does not exceed the per-parent budget.

### Forensic-grade scrub (optional)

For threat models that include "the device is seized after session end and an attacker has cold-boot or DMA capability", an implementation may additionally overwrite cache pages with zeros after CEK destruction. This is **optional** — destroying the CEK alone makes cached content cryptographically unrecoverable, which is enough for the spec's stated threat model. Receivers that want defence in depth against memory-extraction attacks layer the scrub on top.

## Suggested State Machines

### Source State Machine

`Idle -> SessionInit -> ProbeRoute -> BuildParent -> SendOpen -> SendChildren -> SendClose -> AwaitParentAcceptance -> AdvanceRatchet -> ProbeRoute`

Failure branches:

- `ProbeRoute -> RetryAlternateRoute`
- `RetryAlternateRoute -> Delay`
- `Delay -> RestartSession`
- `ChildFailure -> ShredParent -> Rekey -> ContinueNextParent`
- `RepeatedFailureThreshold -> Terminate -> RestartSession`

### Receiver State Machine

`AwaitOpen -> VerifyOpen -> ReceiveChildren -> VerifyChildSet -> VerifyClose -> AcceptParent -> Playback -> ReEncryptConsumedCache -> AwaitOpen`

Failure branches:

- `VerifyOpen fail -> ParentReject`
- `Child fail -> ParentShred`
- `VerifyClose fail -> ParentReject`

### Relay State Machine

A relay node sits in the path between source and receiver(s). It does **not** participate in end-to-end encryption — it cannot decrypt content. It does verify marker signatures (using the source's public identity key, derivable from the publisher_id) and forwards bytes to its downstream peer(s).

`AwaitOpenFromUpstream -> VerifyOpenSignature -> ForwardOpenDownstream -> ReceiveChildrenFromUpstream -> ForwardChildrenDownstream -> AwaitCloseFromUpstream -> VerifyCloseSignature -> ForwardCloseDownstream -> ParentForwardComplete -> AwaitOpenFromUpstream`

Per-parent relay state:

- Upstream peer status (`connected` / `probing` / `lost`)
- Downstream peer set (one or many; per-peer status tracked independently)
- Open marker received (boolean)
- Children received count
- Close marker received (boolean)
- Buffer state (within forwarding window)

Failure branches:

- **Upstream lost mid-parent.** Relay emits a `RelayDisconnect` signal to all downstream peers. The current parent is dropped on the relay's end. Downstream receivers treat this the same as their own upstream peer loss — they shred the in-flight parent and await the next open from a recovered route. Relay returns to `AwaitOpenFromUpstream` and tries to re-establish upstream via alternate-route probing.
- **Downstream peer lost mid-parent.** Relay continues forwarding to remaining downstream peers (in fan-out scenarios). The lost downstream is removed from the per-peer set. No effect on the parent itself.
- **Open marker signature invalid.** Relay drops the parent, does **not** forward to downstream, and demotes the upstream peer's reputation (this is a `red-tendency` signal in the routing model). A relay that forwards a parent it could not verify is itself behaving as a malicious node.
- **Close marker signature invalid.** Same as Open — drop the parent, do not forward the close, demote upstream peer reputation.
- **Children arrive without preceding Open marker.** Drop. A relay does not buffer orphaned children.
- **Out-of-order children within the parent window.** Relay forwards in receipt order; reordering is the receiver's responsibility (the receiver verifies the child set after Close).

Constraints on relay behaviour:

- A relay must never modify packet contents. The verifier-on-receipt step proves this.
- A relay must not synthesise its own markers. Marker signatures are bound to the source's identity key.
- A relay's upstream and downstream peer-sessions are independent. A relay does not relay key material between sessions.
- A relay accumulates `Karma` for delivered children only when both Open and Close markers were forwarded for the same parent. Half-delivered parents do not earn relay rewards (mitigates a relay-ghosting attack where a relay forwards Open + a few children and then disappears, claiming partial credit).

## Suggested Implementation Modules

### Source Side

- Session manager
- Rolling key ratchet
- Parent window planner
- Probe engine
- Secure route selector
- Alternate-route retry budget manager
- Marker builder/verifier
- Shred and rekey coordinator
- Mesh reputation store

### Receiver Side

- Marker validator
- Child verifier
- Playback buffer manager
- Consumed-cache re-encryption worker
- Device-bound rekey acceptance module
- Local participation controller

### Shared or Protocol Modules

- Parent/child metadata schema
- Probe message schema
- Signed reputation exchange schema
- Secure route capability model

## Implementation Requirements

- Parent windows must be first-class objects in code.
- Child validation must be explicit and auditable.
- Probe success must gate parent creation.
- Secure route selection must be bounded by owner-experience policy.
- Receiver cache handling must be isolated and testable.
- Rekey and shred events must be observable in telemetry without leaking private content.
- All route reputation updates must distinguish:
  - disappearance
  - packet loss
  - authenticated corruption

## Telemetry Requirements

Collect only privacy-safe operational telemetry:

- Parent window success/failure counts
- Probe success/failure counts
- Delay durations
- Route failover counts
- Shred counts
- Restart counts
- Child failure classification counts
- Coarse node availability counts

Do not collect:

- Private device reason strings
- Fine-grained owner behavior patterns
- Replay-friendly media data

## Testing Strategy

### Unit Tests

- Key derivation isolation per parent
- Child key derivation correctness
- Marker verification behavior
- Parent shred isolation
- Delay threshold handling
- Restart boundary enforcement

### Integration Tests

- Clean playback across multiple parent windows
- Probe failure then alternate secure route success
- Probe failure then delay
- Child corruption triggering immediate shred
- Clean jump to next verified parent
- Session termination after repeated failures
- Node owner mode change queued until close marker

### Fault Injection Tests

- Packet loss without corruption
- Mid-window node disappearance
- Spoofed probe replies
- Forged open marker
- Forged close marker
- Authenticated child corruption
- Route degradation during long sessions

## Open Design Items

All previously open items have protocol-level defaults. Implementations may override any of them; the defaults exist so that two independent implementations interoperate without prior coordination.

| Item | Default location |
|---|---|
| ~~Exact parent playback duration~~ | "Latency Profiles" — A: 100-150ms, B: 500ms-1s, C: 5-10s |
| ~~Exact child count~~ | "Latency Profiles" — A: 3, B: 3-5, C: 5-10 |
| ~~Whether secure route eligibility classes apply~~ | "Routing Model" — three classes (red/grey/green) are first-class |
| ~~Exact bitrate ladder per profile~~ | "Bitrate Ladder" subsection of "Latency Profiles" |
| ~~Exact rolling failure threshold window~~ | "Rolling Failure Threshold Window" subsection of "Repeated Failure Policy" |
| ~~Exact biometric binding mechanism~~ | "Biometric Binding Mechanism" subsection of "Rekey After Shred" |
| ~~Exact red/grey/green Karma thresholds~~ | "Route-Class Thresholds" subsection of "Routing Model" |
| Exact `Qi` and `Karma` formulas | Live one layer above this protocol — see *Substrate Dependencies* / `IAetherMeshIncentiveProvider`. The streaming protocol consumes Karma as a numeric input; the formulas that produce it are out of scope for this document and are defined by the implementation that owns the reputation primitive. |

## Implementation Prompt

Use the following prompt as the starting brief for implementation planning or code generation:

```text
Design and implement a prototype of an adaptive secure streaming system with these rules:

1. The stream is delivered as fixed logical parent windows, each split into a fixed number of child windows.
2. Each parent window is the trust boundary, the restart boundary, and the playback acceptance boundary.
3. Before every parent window, the source performs a lightweight authenticated probe to measure usable throughput, packet loss, verification time, and secure route availability.
4. The source must not add artificial jitter or fake randomness. It adapts only from real observed network conditions.
5. The source may adapt bitrate/compression for future parent windows, but it may not change parent timeline duration, child count, or the trust model.
6. Each parent derives separate related keys for open marker, payload root, close marker, and each child payload.
7. If any child fails validation, decrypts unexpectedly, or shows authenticated corruption, shred that parent immediately, deny playback from it, trigger background rekey, and continue from the next verified parent window.
8. The player must make a clean jump to the next verified parent with no warning to the user. No rewind, no replay, and no convenience cache are allowed.
9. Consumed cache must be re-encrypted immediately after use to reduce forensic recovery risk.
10. If probe failure prevents safe delivery, the source may try a very small number of alternate secure routes within strict route-count and time budgets. If none succeed, delay the next parent window rather than sending risky data.
11. If the adaptive max delay is exceeded, terminate the session and restart with fresh session material from the nearest future safe parent-window boundary.
12. The routing layer must support decentralized mesh routing with per-hop reputation inputs, but secure traffic must avoid red nodes entirely. Detailed Qi and Karma formulas are intentionally left unspecified and should be represented behind interfaces.
13. Node-owner experience and privacy are hard constraints. Device participation must scale down automatically based on local real-time signals like battery, thermal state, bandwidth, and active use. The mesh must see only coarse availability states: available, limited, or unavailable.
14. Owner mode changes must be queued and applied only after the authenticated close marker of the current parent window is processed, unless the device disappears entirely.

Produce:
- A clear architecture
- Core domain models
- State machines for source and receiver
- Protocol message shapes
- Key derivation interfaces
- Routing and probe interfaces
- Cache lifecycle rules
- Failure-handling flows
- Testing strategy

Keep the design modular so that cryptography, routing, reputation, and playback can evolve independently.
```
