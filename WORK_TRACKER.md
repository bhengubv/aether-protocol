# AetherNet Substrate — Work Tracker

Living checklist derived from `aether-protocol/SUBSTRATE_TASKS.md`, grounded in real file anchors and
reconciled with the current code. Check items off as they ship; when a `[x]` item is a ledger entry,
move it to `thegeeknetwork/AppInfo/AetherNet/01_CURRENT_STATE.md` and delete it here. The tracker
shrinks toward empty.

**Status tags:** `TODO` · `PROVE` (coded, never run for real) · `BLOCKED` (waiting on a gate) ·
`DECIDE` (needs an owner call first) · `DONE`.
**Size:** S / M / L / XL (ledger scale).

**Progress:** 14 shipped (Reach, concurrency test, Phase A ×5, D1–D3, D4 ports ×4) · 5 prove · ~26 build · 3 decisions open.

Constraints in force on every item: `aether://` only · Android + Circle OS · one APK · no GMS · FOSS
only · substrate-first, economy-last · .NET 10 · `[skip ci]` · P30 is the benchmark · code in
`aether-protocol/main`, reusable in `src/`, app-only in `samples/`.

---

## ✅ Shipped this session (verify, then move to the ledger)

- [x] **Reach** — copy-link + keep-an-unreachable-address ("Waiting for"). `src/AetherNet.Browser/Wanted.cs`, proven on P30.
- [x] **Deterministic concurrency test** — scoped to the corruption class; 5/5 with lock, 3/3 without. `AetherStoreConcurrencyTests.cs`.

## Ledger corrections found while tracking (so nobody re-does settled work)

- [x] Scoped-CSS trap — already migrated except one dead rule (see A2).
- [x] `aethernet://` correction — **not in this repo**; lives in the external AR/mapping draft. Not our task.
- ERID — far more built than the ledger says; only the data-plane swap remains (see Phase E).
- `MeshInvariants` predicates — exist; only runtime callers missing (see Phase J).
- `MeshIdentity.DeriveAetherTag` doesn't exist → it's `AetherNetTag.FromPublicKey`.
- Cards aren't script-free → two first-party inline scripts in `CardPage.cs` (matters for Phase F).

---

## Phase A — Fix batch  ·  no deps  ·  ✅ shipped `b036352`

- [x] **A1 · Autosave a draft before Publish** — `Save()` now self-guards (redraws when nameless) and every caller saves unconditionally. **Verified on P30**: a card typed but never published persists as a DRAFT and survives a force-stop + relaunch (SQLite `live=false`). `CardEditor.razor`. · **S · DONE**
- [x] **A2 · Delete the dead scoped rule** — removed from `AetherBrowser.razor.css`; build clean. · **S · DONE**
- [x] **A3 · Distinguish my pages from held cards** — breadcrumb now shows the author's tag beside a held card (`<span class="bm-by">`). Build-verified; live check awaits a held "me" card from a 2nd device (Phase B). `AetherBrowser.razor`. · **S · DONE (code)**
- [x] **A4 · Avatar palette onto the three colours** — eight computed shades of `#2196F3`; new hue-lock test asserts channel ratios. 16/16 deterministic. `TagPalette.cs` + `TagPaletteTests.cs`. · **S · DONE**
- [x] **A5 · Surface the stylesheet pen** — `Style` button on the top toolbar jumps to the CSS field ("Make it yours — CSS"). **Verified on P30**: `Style` present in the editor toolbar. `CardEditor.razor` + `aether-code.js`. · **S · DONE**

## Phase B — Prove what's built  ·  needs a 3rd handset  ·  runs alongside A

- [ ] **B1 · Gateway egress** — one phone as internet egress; the "switch on relaying" nobody has pressed. · **S · PROVE**
- [ ] **B2 · Three-device relay** — `MeshRelay` has never carried for a third node. · **M · PROVE**
- [ ] **B3 · Clean `-t:Install`** — everything went out over `adb install -r`. · **S · PROVE**
- [ ] **B4 · Background-radio-cost soak** — a phone left running a day. · **M · PROVE**
- [ ] **B5 · Role handover in the field** — proven in `RoleFollowsTheRadioTests`, never fired on a device that truly can't host or advertise. · **S · PROVE**

## Phase C — Finish the spine: AI card design (item 7)  ·  needs CircleAI/B!

- [ ] **C1 · AI-assisted card design** — B! proposes blocks with you; additive, never auto-generates, never invents a fact. Seam: `IAetherAiProvider` (`src/AetherNet.Core/Extensibility/`). · **M · TODO**

## Phase D — Promote rendezvous into the protocol  ·  the divergence that matters  ·  D1–D3 shipped

- [x] **D1 · Move `Meeting` + `GroupRole` into `src/AetherNet.Core`** — now `AetherNet.Rendezvous` (HKDF-SHA256, `info="aether-meeting-v1"`, verbatim). Shipped `16ec480`; 58 derivation/role tests pass unchanged, Android head builds. · **M · DONE**
- [x] **D2 · Move `RadioChoice` into `src/AetherNet.Transport`** — now `AetherNet.Transport.Services` (widest-measured-wins, 1.25× hysteresis). New `tests/AetherNet.Transport.Tests` (9/9) registered in the slnx. Shipped `49b892a`. · **M · DONE**
- [x] **D3 · Byte-parity fixtures for the derivation** — `fixtures/meeting/meeting_basic.json` from the C# reference + `MeetingFixtureGenerator` (generate+self-check, same pattern as `CrossLangFixtureGenerator`); Go isn't on PATH and C# is the source-of-truth per the repo's own convention. Adversarial cases + a `rejects` list. Shipped `d613165`. · **M · DONE**
- [~] **D4 · Port the rendezvous derivation (`Meeting` + `GroupRole`) to the 7 other languages** against `fixtures/meeting/`. **4/7 done & verified byte-for-byte on this box** — Python `1e87c53`, Go `edd751c`, Rust `66cb387`, TypeScript `30b9b4f` (each: `Meeting.with` + `hosts_the_group`, HKDF-SHA256, the .NET mixed-endian UUID, address at 8/16/24/32 bits, swapped-pair invariant, rejects). **Remaining on the Mac/.201:** C (C→Mac rule), Kotlin (no `kotlinc` here), Swift (Mac only) — the fixture is their contract. `RadioChoice` (behavioural, no byte fixture) + role-handover ports are a separate follow-on. · **L · IN PROGRESS (4/7)**

## Phase E — Finish ERID on the data plane  ·  #1 CRITICAL  ·  BLOCKED: two-node delivery test

Primitive, in-session exchange (`AddErid()`, `EridDirectory`, `EridExchangeCoordinator`), and BLE rotation already exist.

- [ ] **E1 · Route tables keyed on ERID**, TTL ≤ epoch length. · **L · TODO**
- [ ] **E2 · Remove stable `SourceUhid`/`DestinationUhid` from the header** behind the negotiated `erid-routing` capability. `PacketSerializer.cs:84-94`, `MeshPacket.cs:209/212`. · **M · BLOCKED (delivery test)**
- [ ] **E3 · Keep reputation/incentive state on long-term identity**, never the wire ERID. · **M · TODO**

## Phase F — Mesh-web renderer surface (§2)  ·  builds on `CardPage.cs`

- [ ] **F1 · JS sandbox + `mesh.*` bridge** (fetch/publish/pay/sign/identity) — none exists; builds on the two first-party inline scripts already in `CardPage.cs`. · **XL · TODO**
- [ ] **F2 · Per-site derived identity** — a card sees a per-site key, not the master tag. · **M · TODO**
- [ ] **F3 · Inline `<aether-pay>` / `<aether-vouch>` tags.** · **M / S · TODO**

## Phase G — Reach at true distance (relay §3a)  ·  BLOCKED: two-node hole-punch test

Lights up the "Waiting for" list already built (`Wanted.cs`) — kept addresses resolve on their own once relay exists.

- [ ] **G1 · Two-node libp2p hole-punch** (the gate). · **S · BLOCKED (the test is the gate)**
- [ ] **G2 · AetherTag ↔ PeerID mapping.** · **M · TODO**
- [ ] **G3 · SFrame blind-forward loop** (one peer relays two, can't read them). · **L · TODO**
- [ ] **G4 · Peer-relay election + handoff.** · **L · TODO**

## Phase H — Identity & accounts (§3b)  ·  DECIDE first (recovery, email placement)

Identity/keystore/tag are real; accounts absent; biometric unlock deliberately un-wired (`AndroidKeystoreVault.cs:156`).

- [ ] **H1 · Local-account store (SQLite).** · **M · TODO**
- [ ] **H2 · Wire biometric unlock.** · **M · DECIDE-gated**
- [ ] **H3 · Stateless ownership-validator + sync-auth flow.** · **L · DECIDE-gated**
- [ ] **H4 · Device revocation + PanicWipe pairing.** · **S · TODO**

## Phase I — URI + naming

- [ ] **I1 · Formal `aether://TAG/name` grammar** — today `ContactService.TryParseInvite` reads only `host=TAG` + `?k=`, discards the path. · **S · TODO**
- [ ] **I2 · Per-app handler registration** — filter is baked into one `MainActivity.cs`. · **M · TODO**
- [ ] **I3 · Petname registry** (`Resolve`/`Pin`/`Propose`/`Reject`, gossip, seed bundle). · **M · TODO**
- [ ] **I4 · QR primitive** — generate + scan `aether://` codes. · **S · TODO**

## Phase J — Wire the runtime invariants

- [x] **J1 · Wire the runtime invariants** — relocated the 3 pure predicates to `AetherNet.Core` (`AetherNet.Diagnostics.MeshInvariants`; Content forwards for compat) so low layers can reach them. `OutboxBounded` → `MessagingOutboxHealthCheck`; `WatchTogetherBoundedLatency` → `WatchTogetherService` follower-drift monitor. `ByzantineQuorumReached` has no core aggregation seam (routing uses single-source-signed RREPs) — now reachable for downstream trust-gate consumers rather than faking a caller. 21 tests green, DI graph builds. · **S · DONE**

## Phase K+ — Later layers  ·  map, not sprint  ·  economy-last

- [ ] **K1 · Beacon plane** — unbuilt; the BLE advertiser is GATT-bootstrap only, no data-carrying adverts. · **L · TODO**
- [ ] **K2 · CircleOS to bootable.** · **XL · TODO**
- [ ] **K3 · Gateways** (inbound/outbound/federation/identity). · **M–XL · TODO**
- [ ] **K4 · Federated content** (`aether://wiki / news / learn / music`). · **L each · TODO**
- [ ] **K5 · Family-mode primitives.** · **M–L · DECIDE-gated**
- [ ] **K6 · Hardware SKU** (sub-R1,200 preflashed phone). · **L–XL · TODO**
- [ ] **K7 · Relay-credit → ZAR settlement pipeline.** · **M · TODO**
- [ ] **K8 · Public landing at `aether.thegeek.co.za`.** · **S · TODO**

---

## Gates & open decisions

- [ ] **GATE · Two-node delivery test** — unblocks E2 (ERID header swap).
- [ ] **GATE · Two-node hole-punch test** — unblocks all of Phase G (relay).
- [ ] **DECIDE · Recovery mechanism** (BIP39 / Vault K-of-N / both) — gates H2, H3.
- [ ] **DECIDE · Email/mobile placement** (on-device vs opt-in salted-hash directory) — gates H.
- [ ] **DECIDE · Family-mode's six constitutive questions** — gate K5.

## Recommended order

**A** first (cheap, stops losing drafts) → **E** (critical privacy, now a header swap not a rewrite) →
**D** (unblocks every other language). **B** runs alongside on a spare handset.

## Verification (every code item)

```bash
dotnet build samples/AetherNet.Sample/AetherNet.Sample/AetherNet.Sample.csproj -c Debug -f net10.0-android -m:1 -p:EmbedAssembliesIntoApk=true
```

Tests beside the existing suites (`WantedTests`, `CardTextTests`, `fixtures/*`). Verify on the P30
(`UTKDU19919000815`) via the WebView devtools bridge — **read the SQLite state, not just the screen**
(the reach bug this session was invisible on screen, obvious in the DB). Two phones for anything that
crosses a link; a third for Phase B.
