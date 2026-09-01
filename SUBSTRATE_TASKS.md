# Substrate — Task List

What the sample app owes, measured against the AetherNet ledger documents in
`thegeeknetwork/AppInfo/AetherNet/` (ledger last reviewed 2026-08-09; this file
updated 2026-09-01).

Every task line leads with its **state**:

- **BUILD** — on the plan, not started.
- **PROVE** — coded and deployed, never once exercised for real.
- **FIX** — a known defect.
- **ADD** — genuinely absent from the ledger; belongs on the plan.
- **DECIDE** — needs an owner decision before code.

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
| 6 | Card builder — `aether://creator/` block-builder | **Built, and rebuilt 2026-09-01** as a real rich text editor: the document IS the editable surface (contenteditable over the block model), selection-anchored bubble for marks + settings, a "+" insert sheet, a look gallery of the author's own page, and the paper wears the card's look while you write. The old wizard/form is gone. Stylesheet pen (`</>`) authors raw CSS on your own card, sandboxed by `CardCss.Safe()`. |
| 7 | AI-assisted card design | **Not started.** |

The ledger's flagged build-delta — *"the sample renderer currently injects HTML
(`MarkupString`) — an XSS / network-egress footgun"* — is **closed**. Cards are
signed JSON typed blocks through one inert renderer under `default-src 'none'`.

---

## 1. FIX — defects found

| State | Item | Complexity |
|---|---|---|
| **FIX** | A new card is not written to the DB until Publish; a tab-away loses the draft. Autosave a draft. | S |
| **FIX** | Scoped-stylesheet trap: rules in `*.razor.css` that target elements JavaScript creates at runtime (pen spans, the paper, easing tokens) silently match nothing — Blazor rewrites the selector with a scope attribute the JS element never carries. Bit the token colouring, the prose height cap, and the whole motion pass. Audit remaining such rules; move to the global sheet. | S |
| **FIX** | Breadcrumb row conflates *my pages* with *held cards* — both can be named `me`. Disambiguate. | S |
| **FIX** | `TagPalette` generates 10 off-palette avatar colours, breaking the three-colour rule. | S |
| **FIX** | merlin is on an older build than the P30; redeploy so the two phones match. | S |
| **FIX** | Correct `aethernet://` → `aether://` wherever it appears (the AR/mapping draft; ledger §12). | S |

---

## 2. ADD — absent from the ledger, belongs on it

The one gap the eight docs do not own. A device hosting a card is a server; a
server with no way to be reached and nothing coming back is a server with no
visitors. §2's hold-and-forward moves a card phone-to-phone once handed over;
§13 admits "no federated index yet." Neither names **reach for a hosted card**.

| State | Item | Complexity |
|---|---|---|
| **ADD** | **Reach** — hand someone your card's address at a distance, not only phone-to-phone `Give`. | M |
| **ADD** | **"Who's holding my card"** — a signal back that someone received or re-served it. The first thing on a hosted page that tells the author anyone saw it, without a central counter (decentralisation-first still holds). | M |

---

## 3. Promote the rendezvous layer out of the sample — **the divergence that matters**

Tonight's work lives only in the C# sample. The ledger's whole premise is
byte-parity across eight languages plus machine-checked models. Rendezvous is
protocol, not application: while it stays here, the ports drift and no other
implementation can meet a C# node on a first contact.

| State | Item | Builds on | Complexity |
|---|---|---|---|
| **BUILD** | Move `Meeting` into the protocol surface. Rendezvous derived from the two AetherTags via HKDF-SHA256, ordered so both ends compute the same string. Currently `samples/…/Services/Meeting.cs`. | `AetherNet.Core` identity, `AetherNet.Security` | M |
| **BUILD** | Wire fixtures for the derivation. A tag pair in, a rendezvous string out, byte-identical across all 8 SDKs — the same rig as `fixtures/webrtc/`. Without this the ports diverge silently. | existing fixture rig | M |
| **BUILD** | Port to the other 7 languages. Go, Python, TypeScript, Rust, Kotlin, Swift, C. | fixtures above | L |
| **BUILD** | `RadioChoice` — widest-measured-wins selection with hysteresis. Belongs beside `ITransportSelector`, not in a sample. | `AetherNet.Transport` | M |
| **BUILD** | The Wi-Fi transport. Two phones already on one network is the fastest link in most rooms and the protocol has no transport for it. | `AetherNet.Transport` | M |
| **BUILD** | Role handover. A role assigned by tag ordering must move when the device cannot play it — see `RoleFollowsTheRadioTests`. Same rule in every port. | `Meeting`, transport layer | M |

---

## 4. The mesh-web renderer surface (§2 AetherView) — still to build

The card renderer lives in this repo. CSS authoring is done; the rest of §2 is
not.

| State | Item | Builds on | Complexity |
|---|---|---|---|
| **BUILD** | JS sandbox with a `mesh.*` bridge — no `XHR`/third-party `fetch`; replace with `mesh.fetch(hash)`, `mesh.publish(bytes)`, `mesh.pay(key, amount)`, `mesh.sign(content)`, `mesh.identity()`. | a managed JS engine, the inert renderer | XL |
| **BUILD** | Per-site derived identity — a card sees a deterministic per-site key, not the master AetherTag; cross-site tracking structurally impossible. | `AetherNet.Security` key derivation | M |
| **BUILD** | Inline `<aether-pay amount to>` — one-tap tip / paywall unlock settling via SDPKT. | SDPKT, `IAetherNetIncentiveProvider` | M |
| **BUILD** | Inline `<aether-vouch>` — trust badge read from the local trust graph. | `IReputationGossipService` | S |

---

## 5. ERID — rotating ephemeral routing IDs (T2)

Still the **#1 CRITICAL** in `PRIVACY_THREAT_MODEL.md`: a stable, cleartext,
formerly phone-derived routing identifier lets a passive observer follow a node
forever. `IIdentityService.RoutingKey` and `WireAddress` exist; epoch rotation on
the wire does not.

| State | Item | Builds on | Complexity |
|---|---|---|---|
| **BUILD** | `ERID(epoch) = base32(HMAC-SHA256(routingKey, epoch))[:16]`, 15-minute epochs, matching the BLE ephemeral-ID rotation already in place. | `RoutingKey`, `AetherNet.Security` | M |
| **BUILD** | In-session ERID schedule exchange — peers learn each other's next-N ERIDs inside the encrypted channel; outsiders see uncorrelated 16-char strings. | Signal session | M |
| **BUILD** | Route tables keyed on ERID, route TTL ≤ epoch length so rotated-out routes expire on their own. | routing layer | L |
| **BUILD** | Reputation and incentive state keyed on long-term identity, never the wire ERID — trust survives rotation. | reputation services | M |
| **PROVE** | Two-node delivery test as the gate. ERID rides alongside the current identifier until a real pair proves it delivers. | two phones | S |

---

## 6. The beacon plane

`README.md` describes two link modes. Only the bulk plane exists here — BLE is
GATT-only, and the connectionless plane is unbuilt.

| State | Item | Builds on | Complexity |
|---|---|---|---|
| **BUILD** | Bit-packed stateless advertisements — presence, SOS, "a card exists here" in 31 bytes, no connection. | BLE advertiser | L |
| **BUILD** | Deterministic power-slotting — near-100%-off epochs, GPS-disciplined wake. | platform alarms | L |
| **BUILD** | Slotted-ALOHA collision handling — the radio does not expose carrier-sense, so it is managed at app level. | beacon layer | M |

---

## 7. PROVE — built, never exercised

Coded, deployed, and never once run in anger.

| State | Item | Why unproven | Complexity |
|---|---|---|---|
| **PROVE** | Gateway egress — a node with mobile data as internet egress for its local mesh. Every launch reports *"ready — needs somebody in your Circle to switch on relaying"* and nobody ever has. | needs two phones and one deliberate switch | S |
| **PROVE** | Three devices. A mesh of two is a pair; `MeshRelay` has never carried for a third node. | needs a third handset | M |
| **PROVE** | Background-radio cost (`§13`). Android kills background scan/advertise within minutes; sessions here last minutes, so we have never hit the structural wall. | a phone left running for a day | M |
| **PROVE** | Role handover and the BLE advertise gap. Fixed and tested; neither path can fire on the two phones here, because both can host and one can advertise. | a device that cannot do one of them | S |
| **PROVE** | A clean `-t:Install`. Everything went out over `adb install -r`, which retains data and permissions. Nothing is proven as a clean install. | P30 (merlin's MIUI gate refuses first installs) | S |

---

## 8. Editor polish (this session's leftovers)

| State | Item | Complexity |
|---|---|---|
| **FIX** | Surface the `</>` stylesheet pen; stop burying the one MySpace-lineage feature — raw CSS on your own hosted page — below the fold marked "Optional". | S |
| **BUILD** | UI-string localisation. The whole editor UI is hardcoded English; the audience is South African and multilingual (isiZulu / isiXhosa / Afrikaans / Sesotho first-class per §8). | M |
| **BUILD** | Line numbers / bracket matching / error position in the CSS pen. | S |

---

## 9. Ledger items not started (in this repo)

Straight from `02_REMAINING_WORK.md`, unstarted here. (CircleOS, gateways,
federated content, family-mode, and hardware are ecosystem-scope, not the
sample app's — tracked on the ledger side, not duplicated here.)

| State | Item | Section | Complexity |
|---|---|---|---|
| **BUILD** | `aether://` dispatch — the intent filter exists; the formal grammar and per-app handler registration do not. | §1 | M |
| **BUILD** | QR primitive — generate + scan `aether://` codes, shared across apps. | §1 | S |
| **BUILD** | Petname registry + name client — `Resolve` / `Pin` / `Propose` / `Reject`, gossip propagation, seed bundle. | §3 | M |
| **BUILD** | libp2p relay / connectivity substrate — the two-node DCUtR hole-punch test is the gate before any integration. | §3a | S → XL |
| **BUILD** | Local accounts + biometric unlock + stateless ownership-validator + sync-auth + device revocation. | §3b | S–L |
| **BUILD** | AI-assisted card design — CircleAI / B! composes with you; additive, never auto-generating. | §0a item 7 | M |
| **BUILD** | Runtime predicate wires — `watch-together-timed`, `outbox-backpressure`, `byzantine-routing` are proved in the formal models and unwired at runtime. | §10 | S each |

---

## 10. DECIDE — needs an owner call before code

| State | Item |
|---|---|
| **DECIDE** | Recovery mechanism after last-device loss: BIP39 phrase, Vault K-of-N, or both — and where shards live (`IDENTITY_AND_DATA_SOVEREIGNTY.md §9`). |
| **DECIDE** | Email / mobile placement: on-device-only (Tier A), or an opt-in salted-hash directory so a contact can find you by number. |
| **DECIDE** | Family-mode's six constitutive questions (household root key, per-relationship pseudonymity, parent-visible surface, recovery, divorce, institutional weight) — locked *before* any family code (`02_REMAINING_WORK.md §7`). |

---

## 11. Reconcile the ledger

The ledger's own update protocol says a shipped item moves from
`02_REMAINING_WORK.md` to `01_CURRENT_STATE.md` and is deleted from the backlog.
Six spine items have shipped since its last review and none of them have moved;
the §2 HTML→JSON build-delta is closed but still listed as open.

That edit belongs in `thegeeknetwork`, not here — noted so it is not lost.
