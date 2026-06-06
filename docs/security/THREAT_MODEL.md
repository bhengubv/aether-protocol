# Aether Protocol — Mesh Security Threat Model

> Integration: [Claude-BugHunter](https://github.com/bhengubv/Claude-BugHunter)
> Each threat class maps to a `hunt-*` skill and an `AetherNetVulnerabilityClass` enum value.
> Use `IAetherNetSecurityAudit` to programmatically surface findings in CI or live monitoring.

---

## Attack surface

Aether's attack surface spans four layers:

| Layer | Components | Primary threats |
|---|---|---|
| **Identity** | UHID derivation, Ed25519 keys, AetherNetTag | Spoofing, key compromise |
| **Handshake** | HelloPayload, capability exchange | Auth bypass, downgrade |
| **Routing** | AODV flood, route table, forwarding | Route injection, replay |
| **Content** | Chunk hash, ChunkShuffle, DTN bundles | Poisoning, free-rider, enumeration |

---

## Threat catalogue

### T-01 — UHID Spoofing / Auth Bypass
**BugHunter skill:** `hunt-auth-bypass`
**Class:** `AetherNetVulnerabilityClass.AuthBypass`
**Severity:** Critical

A malicious node presents a forged or replayed UHID during handshake.
If the receiving node does not verify the Ed25519 signature on the HelloPayload,
it accepts a fake identity and grants that node routing access.

**Mitigations:**
- `HandshakeService` must verify `HelloPayload.Signature` against `PeerInfo.PublicKey`.
- `IBiometricProvider` co-presence check (facex) as optional second factor.
- `INodeReputationService` scores new nodes pessimistically until behaviour is observed.

---

### T-02 — Packet Replay
**BugHunter skill:** `hunt-race-condition` (packet-ordering variant)
**Class:** `AetherNetVulnerabilityClass.ReplayAttack`
**Severity:** High

An attacker captures a valid signed packet and re-sends it to re-trigger
an action (re-route, re-deliver a chunk, replay a SOS alert).

**Mitigations:**
- All signed payloads must include a monotonic sequence number or timestamp.
- Receivers must maintain a sliding replay window and reject duplicates.
- `ChunkBitmapPayload.Generation` is a monotonic counter — stale generations
  are already discarded by `ChunkShuffleSession.OnPeerBitmap`.

---

### T-03 — Sybil Attack
**BugHunter skill:** `hunt-business-logic`
**Class:** `AetherNetVulnerabilityClass.BusinessLogic`
**Severity:** High

An attacker creates many fake node identities to gain disproportionate
influence over routing, reputation, or content distribution.

**Mitigations:**
- `INodeReputationService` tracks per-UHID behaviour scores.
- `IBehavioralAnomalyDetector` flags abnormal connection patterns.
- `aether-market` PoVToken requires physical co-presence (BLE) to earn trust.
- `IAetherNetAiProvider.AssessThreatAsync` can correlate Sybil patterns.

---

### T-04 — Malicious Route Injection (AODV Poisoning)
**BugHunter skill:** `hunt-business-logic` (routing variant)
**Class:** `AetherNetVulnerabilityClass.BusinessLogic`
**Severity:** High

A node injects false route advertisements to redirect traffic through itself,
enabling MITM or traffic analysis.

**Mitigations:**
- `IRouteReplyVerifier` validates RREP signatures before updating the route table.
- `IAetherNetAiProvider.SuggestRoutesAsync` cross-checks AI-predicted paths.
- `AetherNetRouteEvent` emitted to `IAetherNetTelemetry` — AI Security Layer detects
  rapid route churn as `AetherNetSecurityEventKind.RoutingAnomaly`.

---

### T-05 — Content Poisoning
**BugHunter skill:** `hunt-business-logic` (content variant)
**Class:** `AetherNetVulnerabilityClass.ContentPoisoning`
**Severity:** High

A malicious peer advertises a `ContentDescriptor` with a valid root hash
but serves corrupted chunk data, aiming to corrupt the receiver's local store.

**Mitigations:**
- `ContentService.HandleChunkDataAsync` verifies SHA-256 of each arriving chunk
  against the descriptor's per-chunk hash before storing.
- Chunks failing verification are discarded and logged; the request is retried
  against a different peer.

---

### T-06 — Free-Rider / Relay Abuse
**BugHunter skill:** `hunt-business-logic` + `hunt-ssrf`
**Class:** `AetherNetVulnerabilityClass.RelayAbuse`
**Severity:** Medium

A node requests relay forwarding without contributing any relaying itself,
or abuses a relay node as an SSRF proxy to reach services not otherwise
accessible to the attacker.

**Mitigations:**
- `IAetherNetIncentiveProvider.ShouldPrioritizeAsync` can deprioritise free-riders.
- `IAetherNetIncentiveProvider.RecordRelayAsync` tracks per-node relay contribution.
- HTTP relay transport (`Aether Purple`) must not forward to private RFC-1918 addresses.

---

### T-07 — NodeCapability Escalation
**BugHunter skill:** `hunt-api-misconfig`
**Class:** `AetherNetVulnerabilityClass.ProtocolMisconfiguration`
**Severity:** Medium

A node claims capabilities (e.g. `NodeCapabilities.Streaming`) it does not
hold, tricking peers into sending traffic it cannot handle.

**Mitigations:**
- Capability flags should be verified in the first protocol exchange that
  exercises them, not just accepted on declaration.
- `IBehavioralAnomalyDetector` flags nodes that advertise capabilities but
  consistently fail to service requests for them.

---

### T-08 — UHID / Content Hash Enumeration
**BugHunter skill:** `hunt-idor`
**Class:** `AetherNetVulnerabilityClass.InformationDisclosure`
**Severity:** Low–Medium

Sequential or predictable UHIDs or content hashes allow an attacker to enumerate
mesh participants or discover private content catalogues without authorisation.

**Mitigations:**
- UHIDs are derived from Ed25519 public keys — not sequential. Brute-force is
  computationally infeasible.
- `IContentService.AnnounceAsync` broadcasts descriptors only to known peers,
  not to unauthenticated nodes.

---

### T-09 — Mesh Flooding / DoS
**BugHunter skill:** `hunt-dos`
**Class:** `AetherNetVulnerabilityClass.DenialOfService`
**Severity:** Medium

A node floods the mesh with AODV requests or `ChunkRequest` packets,
exhausting BLE bandwidth (typically 2–6 Mbit/s shared across all nodes).

**Mitigations:**
- `ProtocolConstants.MaxConcurrentChunkTransfers` caps per-peer in-flight requests.
- `INodeReputationService` penalises nodes with abnormally high packet rates.
- `IBehavioralAnomalyDetector` triggers `AetherNetSecurityEventKind.NodeBehaviourChange`.

---

### T-10 — Malformed Packet RCE
**BugHunter skill:** `hunt-rce`
**Class:** `AetherNetVulnerabilityClass.RemoteCodeExecution`
**Severity:** Critical

A crafted packet with oversized fields, malformed JSON, or type confusion
triggers a parser exception or buffer overflow in a language implementation.

**Mitigations:**
- `PacketSerializer` validates all fields before deserialisation.
- Fuzz test all 8 language implementations against `fixtures/security/fuzz_vectors.json`.
- `IAetherNetSecurityAudit.AuditPacketsAsync` includes a fuzz-pattern scan.

---

### T-11 — Traffic Analysis / Deanonymisation
**BugHunter skill:** `hunt-misc` (timing correlation)
**Class:** `AetherNetVulnerabilityClass.TrafficAnalysis`
**Severity:** Low

Packet timing and size correlation across BLE advertisements can link
pseudonymous UHIDs to physical device locations or social relationships.

**Mitigations:**
- `ChunkBitmapBroadcastCoalesceMs = 500` coalesces bitmap broadcasts,
  reducing timing signal.
- Consider adding random jitter to all broadcast timers in a future release.

---

## BugHunter skill → AetherNetVulnerabilityClass mapping

| BugHunter skill | AetherNetVulnerabilityClass | Threat ID |
|---|---|---|
| `hunt-auth-bypass` | `AuthBypass` | T-01 |
| `hunt-race-condition` | `ReplayAttack`, `RaceCondition` | T-02 |
| `hunt-business-logic` | `BusinessLogic`, `ContentPoisoning` | T-03, T-04, T-05 |
| `hunt-api-misconfig` | `ProtocolMisconfiguration` | T-07 |
| `hunt-ssrf` | `RelayAbuse` | T-06 |
| `hunt-idor` | `InformationDisclosure` | T-08 |
| `hunt-dos` | `DenialOfService` | T-09 |
| `hunt-rce` | `RemoteCodeExecution` | T-10 |
| `hunt-misc` | `TrafficAnalysis` | T-11 |
| `supply-chain-attack-recon` | `SupplyChain` | (all language implementations) |

---

## Using IAetherNetSecurityAudit in CI

```csharp
// In your integration test setup:
services.AddSingleton<IAetherNetSecurityAudit, MyMeshAuditProvider>();

// In your test:
var audit = services.GetRequiredService<IAetherNetSecurityAudit>();
var findings = await audit.AuditPacketsAsync(capturedSession);
Assert.Empty(findings.Where(f => f.IsHighSeverity));
```

## Using Claude-BugHunter for manual audit

1. Install: `git clone https://github.com/bhengubv/Claude-BugHunter.git ~/.claude/skills/Claude-BugHunter`
2. Open aether-protocol in Claude Code
3. Describe what you're testing: *"I see a HelloPayload with no signature field — test auth bypass"*
4. Claude auto-loads `hunt-auth-bypass` and walks you through T-01 patterns
