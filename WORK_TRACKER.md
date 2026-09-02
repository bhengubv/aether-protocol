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

- [x] **E1 · Route tables keyed on ERID, TTL ≤ epoch** — `EridRouteResolver` resolves a received ERID → the stable long-term UHID so a route survives rotation instead of vanishing each window; `RouteEntry.RefreshBoundedBy` + `EphemeralRoutingId.EpochEndUnixSeconds` cap the route at the epoch boundary; wired into `RoutingService` as an opt-in resolver (pass-through by default, so today's UHID wire is unchanged). 87 routing/ERID tests green. Full activation is the E2 cutover. · **L · DONE (opt-in; activation gated on E2)**
- [ ] **E2 · Remove stable `SourceUhid`/`DestinationUhid` from the header** behind the negotiated `erid-routing` capability. `PacketSerializer.cs:84-94`, `MeshPacket.cs:209/212`. E1 makes this a header swap, not a rewrite — the receive side already resolves ERIDs. · **M · BLOCKED (two-node delivery test — needs a 2nd phone)**
- [x] **E3 · reputation/incentive on long-term identity, never the wire ERID** — the stores already key on UHID; the resolver now resolves the accountable/route source to the stable UHID before any reputation, rate-limiter, or route-table key, so an ERID can never leak into an identity ledger. · **M · DONE**

## Phase F — Mesh-web renderer surface (§2)  ·  builds on `CardPage.cs`  ·  DECIDE first (inert vs executable cards)

The render substrate is ~70% there (iframe `srcdoc` under CSP `default-src 'none'`, a working card↔host `postMessage` seam with `[JSInvokable]` dispatch, first-party inline-script delivery). But F1/F3 require **author code to run inside a card**, which contradicts the repeated, deliberate "a card is a document, never a program" design and the standing "a card must stay inert on a stranger's phone" rule. The card iframe is same-origin with **no `sandbox`**, so any author-reachable capability needs sandbox hardening (a cross-origin `sandbox="allow-scripts"` frame) FIRST. That is a first-party security decision, not mine to flip.

- [ ] **F1 · JS sandbox + `mesh.*` bridge** (fetch/publish/pay/sign/identity) — ⚠️ **DECIDE:** do cards run author code at all? If yes, sandbox-harden the frame, then add request/response correlation to the `aether-card.js` ⇄ `aetherCardHost` channel + a `window.mesh` shim. Smallest safe first slice = a read-only `mesh.identity()` returning the F2 pseudonym. · **XL · DECIDE-gated (conflicts with inert-card rule)**
- [x] **F2 · Per-site derived identity** — built: `AetherNet.Identity.SiteIdentity` + `SiteIdentityDerivation.ForSiteAsync(identity, siteTag)` — a per-site pseudonym (tag-shaped) + secret from `INodeIdentity.DeriveKeyAsync("aether-site-identity-v1:"+tag)`. Stable per site, unlinkable across sites, never reveals the master tag. 7 tests. The card-facing *exposure* of it is F1. · **M · DONE (primitive; exposure is F1)**
- [ ] **F3 · Inline `<aether-pay>` / `<aether-vouch>` tags** — backends exist unwired to Browser (`AetherNet.Tipping.TippingService`, `AetherNet.Market.IPoVService`); the tags themselves need F1's bridge + the security decision. · **M / S · DECIDE-gated (needs F1)**

## Phase G — Reach at true distance (relay §3a)  ·  BLOCKED: two-node hole-punch test

Lights up the "Waiting for" list already built (`Wanted.cs`) — kept addresses resolve on their own once relay exists.

- [ ] **G1 · Two-node libp2p hole-punch** (the gate). · **S · BLOCKED (the test is the gate)**
- [ ] **G2 · AetherTag ↔ PeerID mapping.** · **M · TODO**
- [ ] **G3 · SFrame blind-forward loop** (one peer relays two, can't read them). · **L · TODO**
- [ ] **G4 · Peer-relay election + handoff.** · **L · TODO**

## Phase H — Identity & accounts (§3b)  ·  primitives built; account layer + recovery choice remain

Map correction: every H **primitive** already exists and most are fixture-proven — the tracker's "accounts absent" was overstated. BIP39 recovery (`Bip39Mnemonic`/`IdentityBackup`) and Vault K-of-N (`IVaultService` + Reed-Solomon) are both DONE; `PanicWipe` and `DeviceLink` are DONE; a SQLite identity store exists in the sample (`AetherStore`).

- [~] **H1 · Local-account store (SQLite)** — a single-row SQLite `identity` store + `IdentityService` already exist in `samples/…/AetherStore.cs` (public-half mirror; private key stays in the vault). Gaps: no richer account object (display-name/avatar/recovery-state persisted — profile PII is currently in-memory only) and no **online** account. The online-account shape is the **DECIDE · email/mobile placement** gate. · **M · PARTIAL (identity store exists; account object + online acct DECIDE-gated)**
- [ ] **H2 · Wire biometric unlock** — deliberately un-wired at `AndroidKeystoreVault.cs:156` for a real reason (identity is read at app-start and by background mesh with the screen off; a `SetUserAuthenticationRequired` key makes the node refuse to open a sealed identity → app won't start, observed on P30 Lite). Honest wiring = a per-open **foreground** `BiometricPrompt` app-lock that gates user-initiated signing, leaving the keystore flags untouched — Android platform code, needs the device to verify, and DECIDE-gated on recovery. · **M · BLOCKED (Android device + recovery DECIDE)**
- [x] **H3 · Stateless ownership-validator + recovery** — recovery mechanisms both **already built** (BIP39 + Vault K-of-N, fixture-proven). Ownership-validator **built this session**: `AetherNet.Security.Identity.OwnershipValidator` — issue a random nonce → holder `SignAsync` → verifier checks signature + tag binding + freshness and **stores nothing** ("ownership proven, never stored"). Domain-separated, replay-guarded, 8 tests. The admit-device sync-auth flow now has all its pieces (ownership proof + `DeviceLink`); wiring the flow is app-level. Which recovery mechanism an account uses is the **DECIDE · recovery** gate. · **L · DONE (validator); recovery choice DECIDE-gated**
- [x] **H4 · Device revocation + PanicWipe pairing** — device revocation **built this session**: `AetherNet.Security.Sync.DeviceRevocation` + `DeviceRevocationCodec` (signed inverse of `DeviceLink`, domain-separated so a link can't be replayed as a revocation) + `RevocationSet` (admits only validly-signed revocations). Pairs with the existing `PanicWipe`: PanicWipe erases the **local** device under duress, `DeviceRevocation` (gossiped via `SyncRecord`) invalidates a device you **no longer hold**. 7 tests. `PanicWipe`'s app-level trigger wiring (duress-PIN setup, DB/vault wipe) remains an app task. · **S · DONE (record + set; app-trigger wiring remains)**

## Phase I — URI + naming  ·  complete

- [x] **I1 · Formal `aether://TAG/name` grammar** — **already built** (ledger was stale): `AetherNet.Addressing.AetherUri` is a full ABNF grammar (authority = tag or 64-hex UHID, path segments, query, fragment, percent-encoding, canonical round-trip, equality) with `Parse`/`TryParse`/`ToString`. · **S · DONE (already built)**
- [x] **I2 · Per-app handler registration** — **already built**: `IAetherUriRouter`/`AetherUriRouter` + `AetherUriHandlerManifest`/`AetherUriHandlerDescriptor` + `RegisterHandler` + `DispatchAsync` — a manifest of handlers with per-descriptor callbacks, not a baked-in `MainActivity` filter. · **M · DONE (already built)**
- [x] **I3 · Petname registry** — built this session: `AetherNet.Identity.PetnameRegistry` (+ `IPetnameStore`/`InMemoryPetnameStore`). `Pin` (authoritative), `Propose`/`Reject` (peer suggestions that never override a pin), `ResolveName` (unambiguous only, pins win), `NameFor`, `Seed` bundle, `ExportProposals`/`ImportProposals` gossip. Zooko's-triangle, local-first (no central registry). 17 tests green. · **M · DONE**
- [x] **I4 · QR primitive** — `AetherNet.Browser.QrSvg` renders an `aether://` invite as inline SVG (generate). Scanning is **deliberately** the OS camera opening the `aether://` link via the registered scheme handler (I2) — no in-app decoder by design. · **S · DONE (generate; scan via OS + scheme handler)**

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
