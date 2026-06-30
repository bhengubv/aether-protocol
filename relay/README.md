# CircleAether — Decentralised Relay Layer (libp2p)

**Branch:** `aether-decentralised-relay` · **Status:** IN DEVELOPMENT, verified-first.
Nothing is wired into any app yet. Each increment must *run* before the next is built.

The day-one, ubiquitous connectivity + relay substrate for AetherNet — so real-time group
media and NAT-traversed P2P work **before** AetherNet has its own node fabric, by riding
existing global networks (libp2p public nodes, Lokinet) and handing off to our gateway nodes
as they come online. **Additive:** sits *under* the existing mesh transports; replaces nothing.

**Full design + feasibility log:** `AppInfo/AetherNet/DECENTRALISED_RELAY_LAYER.md`
(authored on `txtme-aethernet-default`; merges to master).

## Build order (verified-first — prove each runs before integrating)

| # | Increment | Status |
|---|---|---|
| 0 | Single-node composition (circuit-relay-v2 + webrtc + dcutr + dht + autonat) | ✅ proven |
| 1 | Two independent nodes connect + identify + ping | ✅ proven (below) |
| 2 | Relay + DCUtR hole-punch — A↔B via relay R, upgrade to direct | ◑ relay + DCUtR protocol proven; final dial needs a public IP |
| 3 | AetherTag ↔ libp2p PeerID mapping | ✅ proven (deterministic + pure-function bijection) |
| 4 | SFrame + Insertable-Streams blind forward loop (SFU-as-a-role) | ✅ proven (AES-GCM/frame, 83/83 byte-exact, relay blind) |
| 5 | `TheGeekNetwork.Shared.CircleAether.Relay` .NET service (drives js-libp2p) | ✅ proven (`.NET` built + hosted the relay, live PeerID) |
| 6 | Browser / WebView substrate — js-libp2p boots in-browser | ✅ substrate proven; **not** wired into any app (kept to this branch by instruction) |

## Tests — the green gate (run locally, no CI)

Real automated tests live in [`tests/`](tests/) and run with one command:

```
cd tests && npm install && npm test
```

Result (Node 22, 2026-06-29) — **3 / 3 pass, exit 0**:
- `aethertag-peerid.test.mjs` — same AetherTag seed → identical PeerID; PeerID derivable from the
  pubkey alone; Ed25519 PeerID embeds the pubkey (pure bijection). Pure crypto, no network.
- `relay-leg.test.mjs` — B reserves a slot on relay R; A connects to B **through** R (relayed
  `/p2p-circuit`); DCUtR mounted on both ends.
- `two-node.test.mjs` — two independent nodes connect, identify, and a ping round-trips.

Three further proofs are real pass/fail commands (each exits non-zero on failure; verified green
2026-06-29 — the browser boot run 3× back-to-back, all exit 0):
- **.NET host** — `cd spikes/dotnet-host && npm install && dotnet run -- relay-only.mjs` →
  `GREEN … PeerID = 12D3KooW…`, exit 0 (the .NET ↔ js-libp2p bridge).
- **SFrame blind forward** (headless Chrome) — `cd spikes/sframe && npm install && npm test` →
  `green:true`, exit 0 (every forwarded frame byte-exact, on-wire bytes differ from plaintext —
  the relay is blind). Exits 1 if any frame fails to round-trip.
- **Browser / WebView boot** (headless Chrome) — `cd spikes/browser-node && npm install &&
  npm test` → bundles the js-libp2p browser stack from source with esbuild, boots it in a real
  browser, asserts `status:"started"` + a PeerID (exit 0; exits 1 otherwise; 60s budget + 1 retry).

The one thing that is **not** a dev test is the DCUtR *direct upgrade* — it needs two devices on
different networks, so it is **field / QA** (real-world verification, owned by QA), not part of the
dev green gate. The relay leg it builds on is covered by `relay-leg.test.mjs` above.

## Increment 1 — proof (run 2026-06-29, Node 22)

`spikes/two-node` boots two independent libp2p nodes; A dials B and pings:

```
B (listener): 12D3KooWJZEg3f8rmMekzV7zHjXj5dp8ZA6PkepD1UjGyZwA4VUT
A (dialer) : 12D3KooWS63aepZoBuLf8moCb1M6hLtEukG6cd5ZdP9PtXd7ZJLe
ping RTT (ms): 5   — A connected peers: [B]
```

## Increment 2 — finding (relay leg proven; direct upgrade → 2-device gate)

`spikes/relay-dcutr` stands up a circuit-relay-v2 server R, has B reserve a slot and listen
via `/p2p-circuit`, then A dials B **through** R. Repeatable result (run 2026-06-29):

- ✅ **Relay leg works** — B's reservation succeeds and A→B forms a **relayed** connection over
  `/p2p-circuit`.
- ◑ **DCUtR protocol runs; the final direct dial needs a public address.** Debug logs show the
  Connect/Sync handshake completing both directions, then libp2p declines the dial:
  *"has no public addresses, not attempting"* / *"no dialable multiaddrs"*. Every address on a
  single home-NAT'd box is private (192.168 / 10 / 169.254) and hole-punching only targets
  **public** addresses by design — not a bug, the library's own rule. True green for the dial
  needs one public-facing endpoint (a public-IP host, or the 2-device test); field DCUtR ≈ 70% ± 7.1%.

So increment 2 = the relay machinery (proven) + the hole-punch (correctly deferred to the device
gate — not demonstrable single-host, and not faked).

## Increment 3 — proof (AetherTag ↔ PeerID, GREEN)

`spikes/aethertag-peerid` (run 2026-06-29): same AetherTag seed → identical PeerID across runs; the
PeerID is reconstructable from the public key alone (an AetherTag yields the PeerID for a DHT
lookup); and Ed25519 PeerIDs embed the pubkey — the map is a pure bijection, no lookup table.

```
DETERMINISTIC (same AetherTag seed -> same PeerID): true
AetherTag -> PeerID is a pure function: true
PeerID embeds the pubkey (identity multihash): true
```

## Increment 4 — proof (SFrame blind forward, GREEN)

`spikes/sframe` (headless Chrome, 2026-06-29): a WebRTC loopback AES-GCM-encrypts every **encoded**
frame via `createEncodedStreams` (Insertable Streams), carries it over a live connection (pc1/pc2
both `connected`), and decrypts at the receiver. Result:

```
{ enc: 83, dec: 83, matches: 83, ctDiffers: 83, green: true }
```

83 frames encrypted → forwarded → decrypted **byte-exact** (`matches == enc`), and every on-wire
frame differed from plaintext (`ctDiffers == enc`) — i.e. a relay forwarding these frames is blind.
That is the SFU-as-a-role mechanism: forward encrypted encoded frames without decoding. (Harness
cipher = AES-GCM; production swaps in the SFrame ratchet over the same transform.)

## Increment 5 — proof (.NET hosts js-libp2p, GREEN)

`spikes/dotnet-host` (run 2026-06-29): a real .NET console (`dotnet run`) launched the js-libp2p
circuit-relay-v2 server and captured its live PeerID:

```
GREEN - .NET hosted the js-libp2p relay; PeerID = 12D3KooWBDoTkz2XtNFf28gwKzoc6c1Lcf2htfzWLw4z6LuzqBRu
```

This proves the `.NET ↔ libp2p` bridge. In the txtMe **Blazor WebView** the same js-libp2p runs
*inside* the WebView (no sidecar); the standalone `.NET` host is the path for non-WebView hosts.

## Increment 6 — proof (js-libp2p boots in the WebView substrate, GREEN)

`spikes/browser-node` (headless Chrome, 2026-06-29): the js-libp2p **browser** stack
(WebRTC + WebSockets + circuit-relay transports) is bundled with esbuild (1.3 MB) and **booted
inside a real browser** — which is exactly txtMe's BlazorWebView runtime:

```
{ peerId: "12D3KooWECwsWJwUsN3jLJfHu1USyoZqNJcCsFsiWLPkAYw2zo7n", status: "started", services: ["identify","dcutr"] }
```

This proves the substrate runs in a WebView. It is **not** wired into txtMe (or any app) — that
integration is a separate, approved task and intentionally stays out of this branch. (An earlier
wire into txtMe was reverted; the relay layer lives only here until integration is greenlit.)

## Running a spike

```
cd spikes/two-node && npm install && node two-node.mjs
```

Node 18+; ESM. `node_modules` is git-ignored.

## Honest scope

Local spikes prove the **code path** — the APIs compose and run between real nodes. They do
**not** prove field NAT hole-punching; that needs two devices on different networks (field
DCUtR success ≈ 70% ± 7.1%, per the design doc's measurement). The 2-device test is the real
gate before any production claim.
