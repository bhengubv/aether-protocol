# Substrate — Task List

What the sample app owes, measured against the AetherNet ledger documents in
`thegeeknetwork/AppInfo/AetherNet/` (read 2026-08-30; those docs were last
reviewed 2026-08-09).

Complexity uses the ledger's own S / M / L / XL scale. This file shrinks toward
empty; when an item ships, delete it here and move it to `01_CURRENT_STATE.md`
on the ledger side.

---

## 0. Where the substrate MVP actually stands

`02_REMAINING_WORK.md §0a` lists a seven-item spine. Six are built:

| # | Item | State |
|---|---|---|
| 1 | Connection — wizard, all radios, unskippable | **Built and proven.** Two phones with cleared contacts, sessions and routing keys linked from typed tags alone. |
| 2 | Voice 1:1 | Built. Sealed — master key via Signal, frames under it. |
| 3 | Chat 1:1 → 1:many | Built. |
| 4 | Video 1:1 → 1:many over Wi-Fi Direct | Built, sealed, group frames included. |
| 5 | Hosted card — device serves its own signed JSON card | Built. |
| 6 | Card builder — `aether://creator/` block-builder | Built. |
| 7 | AI-assisted card design | **Not started.** |

The ledger's flagged build-delta — *"the sample renderer currently injects HTML
(`MarkupString`) — an XSS / network-egress footgun"* — is **closed**. Cards are
signed JSON typed blocks through one inert renderer under
`default-src 'none'`.

---

## 1. Promote the rendezvous layer out of the sample — **the divergence that matters**

Tonight's work lives only in the C# sample. The ledger's whole premise is
byte-parity across eight languages plus machine-checked models. Rendezvous is
protocol, not application: while it stays here, the ports drift and no other
implementation can meet a C# node on a first contact.

| Item | Builds on | Complexity |
|---|---|---|
| **Move `Meeting` into the protocol surface.** Rendezvous derived from the two AetherTags via HKDF-SHA256, ordered so both ends compute the same string. Currently `samples/…/Services/Meeting.cs`. | `AetherNet.Core` identity, `AetherNet.Security` | M |
| **Wire fixtures for the derivation.** A tag pair in, a rendezvous string out, byte-identical across all 8 SDKs — the same rig as `fixtures/webrtc/`. Without this the ports will diverge silently. | existing fixture rig | M |
| **Port to the other 7 languages.** Go, Python, TypeScript, Rust, Kotlin, Swift, C. | fixtures above | L |
| **`RadioChoice` — widest-measured-wins selection with hysteresis.** Belongs beside `ITransportSelector`, not in a sample. | `AetherNet.Transport` | M |
| **The Wi-Fi transport.** Two phones already on one network is the fastest link in most rooms and the protocol has no transport for it. | `AetherNet.Transport` | M |
| **Role handover.** A role assigned by tag ordering must be able to move when the device cannot play it — see `RoleFollowsTheRadioTests`. Same rule needs to hold in every port. | `Meeting`, transport layer | M |

---

## 2. ERID — rotating ephemeral routing IDs (T2)

Still the **#1 CRITICAL** in `PRIVACY_THREAT_MODEL.md`: a stable, cleartext,
formerly phone-derived routing identifier lets a passive observer follow a node
forever. `IIdentityService.RoutingKey` and `WireAddress` exist; epoch rotation on
the wire does not.

| Item | Builds on | Complexity |
|---|---|---|
| **`ERID(epoch) = base32(HMAC-SHA256(routingKey, epoch))[:16]`**, 15-minute epochs, matching the BLE ephemeral-ID rotation already in place. | `RoutingKey`, `AetherNet.Security` | M |
| **In-session ERID schedule exchange** — peers learn each other's next-N ERIDs inside the encrypted channel; outside observers see uncorrelated 16-char strings. | Signal session | M |
| **Route tables keyed on ERID**, route TTL ≤ epoch length so rotated-out routes expire on their own. | routing layer | L |
| **Reputation and incentive state keyed on long-term identity, never the wire ERID** — trust survives rotation. | reputation services | M |
| **Two-node delivery test as the gate.** The design says migration is gated on a delivery test, not on sign-off: ERID rides alongside the current identifier until a real pair proves it delivers. | two phones | S |

---

## 3. The beacon plane

`README.md` describes two link modes. Only the bulk plane exists here — BLE is
GATT-only, and the connectionless plane is unbuilt.

| Item | Builds on | Complexity |
|---|---|---|
| **Bit-packed stateless advertisements** — presence, SOS, "a card exists here" in 31 bytes, no connection. | BLE advertiser | L |
| **Deterministic power-slotting** — near-100%-off epochs, GPS-disciplined wake. | platform alarms | L |
| **Slotted-ALOHA collision handling** — the radio does not expose carrier-sense, so it is managed at app level. | beacon layer | M |

---

## 4. Prove what is built but never exercised

Coded, deployed, and never once run in anger.

| Item | Why it is unproven | Complexity |
|---|---|---|
| **Gateway egress.** A node with mobile data as internet egress for its local mesh. Every launch reports *"ready — needs somebody in your Circle to switch on relaying"* and nobody ever has. | needs two phones and one deliberate switch | S |
| **Three devices.** A mesh of two is a pair. `MeshRelay` has never carried for a third node. | needs a third handset | M |
| **Background-radio cost** (`§13`). Android kills background scan/advertise within minutes; sessions here last minutes, so we have never hit the wall the ledger says is structural. | a phone left running for a day | M |
| **Role handover and the BLE advertise gap.** Fixed and tested today; neither path can fire on the two phones here, because both can host and one can advertise. | a device that cannot do one of them | S |
| **A clean `-t:Install`.** Everything on the phones tonight went out over `adb install -r`, which retains data and permissions. Nothing is proven to be a clean install. | P30 (merlin's MIUI gate refuses first installs) | S |

---

## 5. Ledger items not started

Straight from `02_REMAINING_WORK.md`, unstarted here.

| Item | Section | Complexity |
|---|---|---|
| **`aether://` dispatch** — the intent filter exists; the formal grammar and per-app handler registration do not. | §1 | M |
| **Petname registry + name client** — `Resolve` / `Pin` / `Propose` / `Reject`, gossip propagation, seed bundle. | §3 | M |
| **libp2p relay / connectivity substrate** — the two-node DCUtR hole-punch test is the gate before any integration. | §3a | S → XL |
| **AI-assisted card design** — CircleAI / B! composes with you; additive, never auto-generating. | §0a item 7 | M |
| **Runtime predicate wires** — `watch-together-timed`, `outbox-backpressure`, `byzantine-routing` are proved in the formal models and unwired at runtime. | §10 | S each |

---

## 6. Reconcile the ledger

The ledger's own update protocol says a shipped item moves from
`02_REMAINING_WORK.md` to `01_CURRENT_STATE.md` and is deleted from the backlog.
Six spine items have shipped since its last review and none of them have moved.

That edit belongs in `thegeeknetwork`, not here — noted so it is not lost.
