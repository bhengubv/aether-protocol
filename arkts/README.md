# Aether Protocol — ArkTS (HarmonyOS) Implementation

The **9th language implementation** of the three AetherNet money/vault features,
byte-identical to the C# reference and proven against the shared cross-language
fixtures. ArkTS is HarmonyOS's strict TypeScript superset; this module is an
OpenHarmony **shared library (HAR)**.

| Feature   | Packet / codec                                   | Source |
| --------- | ------------------------------------------------ | ------ |
| Tipping   | `TipPacket = 24`                                 | `src/main/ets/incentive/` |
| Vault     | systematic Cauchy-Reed-Solomon `K=10, M=4` over GF(2⁸) (`0x11D`, α=2) | `src/main/ets/vault/` |
| Market    | Proof-of-Vicinity `PoVTokenExchange = 43`        | `src/main/ets/market/` |

Real, deterministic **Ed25519** via [`@noble/ed25519`](https://github.com/paulmillr/noble-ed25519)
(pure JavaScript, byte-stable under both ArkTS and Node). **No stubs, no
placeholder crypto.** Because the same deterministic library runs in both places,
this port *regenerates* the C# fixture signatures byte-for-byte — not merely
verifies them.

## Module structure

```
arkts/
├── oh-package.json5                      # HAR manifest + deps (@noble/ed25519, @noble/hashes)
├── build-profile.json5                   # module build profile (ArkTS strictMode on)
├── hvigorfile.ts                         # hvigor harTasks entry
├── Index.ets                             # library entry — re-exports the public API
├── README.md                             # this file
│
├── src/main/
│   ├── module.json5                      # HAR module descriptor (type: "har")
│   └── ets/
│       ├── protocol/
│       │   ├── PacketType.ets            # PacketType enum (TipPacket=24, PoVTokenExchange=43)
│       │   ├── MeshPacket.ets            # mesh envelope model
│       │   ├── Bytes.ets                 # UTF-8 / hex / base64 (Buffer-free, ArkTS+Node)
│       │   └── Ed25519Provider.ets       # @noble-backed deterministic Ed25519 (only crypto dep)
│       ├── incentive/
│       │   ├── TipPacketPayload.ets      # canonical bytes + JSON wire form
│       │   ├── MeshTipService.ets        # send/handle TipPacket(24), settlement hook
│       │   └── index.ets
│       ├── vault/
│       │   ├── ReedSolomonCodec.ets      # GF(2⁸) Cauchy-RS encode + Gauss-Jordan decode
│       │   ├── VaultCodec.ets            # split / encode / reconstruct helpers
│       │   └── index.ets
│       └── market/
│           ├── PoVToken.ets              # canonical body + i64-ticks lossless JSON
│           ├── PoVTokenExchangeService.ets # packet-43 exchange, countersign, replay-dedup
│           └── index.ets
│
├── src/ohosTest/                         # on-device Hypium tests (the gated build step)
│   ├── module.json5
│   └── ets/test/
│       ├── List.test.ets                 # suite aggregator
│       └── Parity.test.ets               # byte-parity asserts (fixtures embedded inline)
│
└── test/                                 # Node parity harness (NOT part of the HarmonyOS build)
    ├── package.json
    ├── build-ets.ts                      # esbuild bundler: .ets → ts, resolves @noble
    └── parity.test.ts                    # the parity proof — runs under Node/tsx
```

## Parity proof (runs here, under Node)

> There is **no DevEco Studio / HarmonyOS SDK / hvigor on this build box**, so the
> on-device HAP/HAR build cannot run here. ArkTS is ~TypeScript, so the **core
> logic is proven byte-for-byte under Node** against the shared fixtures. Only the
> HarmonyOS-runtime/hvigor build is gated.

The harness bundles the **actual `.ets` sources** with esbuild (`.ets` is loaded as
TypeScript; ArkTS's `ESObject` annotations are erased) and asserts byte-identity
against `fixtures/{tipping,vault,market}/*.json`.

```bash
cd arkts/test
npm install                 # @noble/ed25519, @noble/hashes, esbuild
npm run parity              # or: npx tsx parity.test.ts
#   VERBOSE=1 npx tsx parity.test.ts   # list every assertion
```

**Latest result: `80 passed, 0 failed`** — Tipping 25, Vault 27, PoV 28.

It proves:

- **Canonical bytes byte-identical** — tip payload canonical data and PoV canonical
  body match every fixture vector (LE i32 length prefixes; `amount` as the invariant
  decimal **string**; null `reference_id` → 16 zero bytes; present id in .NET
  mixed-endian GUID order; i64 LE timestamp/ticks via `bigint`).
- **Deterministic Ed25519 reproduces the fixture signatures exactly** — the derived
  public key and every signature equal the fixture, and every fixture signature
  **verifies**.
- **Reed-Solomon**: every systematic data shard and every Cauchy parity shard is
  byte-identical; every K-of-N recovery subset (systematic fast-path *and*
  matrix-inversion path) decodes to the fixture input; **K-1 survivors fail**.
- **i64 ticks beyond `Number.MAX_SAFE_INTEGER`** survive a JSON round-trip exactly
  (the literal is spliced, never coerced through a double).
- **Service dispatch**: `MeshTipService` emits a `TipPacket(24)` carrying the exact
  fixture signature and routes it; inbound tips reach the settlement hook;
  malformed-signature tips are dropped first. `PoVTokenExchangeService` issues a
  directed `PoVTokenExchange(43)` (TTL 1), the subject verifies + countersigns +
  records, a replay is rejected, and self-vouch / non-short-range minting is refused.

## On-device build & test (GATED — requires DevEco Studio / hvigor)

This box has no HarmonyOS toolchain. A HarmonyOS developer builds and runs the
on-device Hypium suite as follows.

**Prerequisites**
- DevEco Studio 5.0.1+ (HarmonyOS SDK / OpenHarmony API 13+), which bundles the
  `hvigor` / `hvigorw` build tool and the `ohpm` package manager.

**Install dependencies (resolves `@noble/ed25519`, `@noble/hashes`, `@ohos/hypium`)**
```bash
cd arkts
ohpm install
```

**Build the HAR (library) — debug and release**
```bash
# from arkts/ (or the workspace root once this module is registered in the project)
hvigorw assembleHar --mode module -p product=default
hvigorw assembleHar --mode module -p product=default -p buildMode=release
# output: arkts/build/default/outputs/default/aether_protocol_arkts.har
```

**Run the on-device / emulator Hypium parity suite**
```bash
# with a HarmonyOS device or emulator connected (hdc devices)
hvigorw onDeviceTest --mode module -p product=default
# DevEco Studio equivalent: right-click src/ohosTest → "Run Tests",
# or use the gutter Run icons on Parity.test.ets.
```

The Hypium suite (`src/ohosTest/ets/test/Parity.test.ets`) embeds the fixture
vectors inline and asserts the same canonical-byte / deterministic-signature /
Reed-Solomon parity as the Node harness, so the on-device run confirms the ArkTS
toolchain produces identical bytes.

> Note: `oh_modules/` and `arkts/build/` (and the harness `test/node_modules/`) are
> build artifacts and should not be committed.

## ArkTS strictness

The sources are written to ArkTS's stricter-than-TypeScript rules:

- **No `any`**, explicit types on every declaration, parameter, and return.
- **Classes over object-literal types** — e.g. `TipPacketPayloadInit`,
  `PoVTokenInit`, `PoVScore`, and the `*Init` parameter objects are classes, not
  inline `{...}` types. Interfaces are used only for behavioural seams
  (`TipMeshSender`, `PoVPacketSigner`, `Ed25519Signer`, …).
- **`Uint8Array` / `ArrayBuffer` / `DataView`** for all byte work; **`bigint`** for
  every i64 (timestamps, .NET ticks). Little-endian integer writes use `DataView`.
- **No `Buffer`** (it does not exist in ArkTS) — UTF-8, hex, and base64 are
  hand-rolled in `Bytes.ets`, identical across ArkTS and Node.
- **No unsafe `as` casts.** The only `as` usages are the ArkTS-sanctioned dynamic
  JSON pattern: `JSON.parse(text) as Record<string, ESObject>` followed by typed
  field reads (`ESObject` is ArkTS's controlled dynamic type, **not** `any`), and
  numeric-enum narrowing of a validated byte.
- The crypto backend is isolated behind the `Ed25519Signer` interface in a single
  file (`Ed25519Provider.ets`); everything else is dependency-free, so the backend
  can be swapped for `@ohos.security.cryptoFramework` on a device build without
  touching protocol logic.

### Type-checking (also runnable here)

The parity harness uses esbuild, which **erases** type annotations without checking
them — so on its own it proves runtime byte-parity, not type conformance. A second
tool closes that gap **under Node**:

```bash
cd arkts/test
npm run typecheck           # or: npx tsx typecheck.ts
```

It shadow-copies the `.ets` sources to `.ts`, supplies an ambient `ESObject` typed
as **`unknown`** (the strictest reading — every dynamic-JSON read must be narrowed
via a `typeof` guard, exactly as the sources do), and runs `tsc --strict --noEmit`.
**Latest result: 0 type errors.**

### What only a real DevEco compile can additionally confirm

`tsc --strict` covers the type system; what remains gated to a DevEco / `hvigor`
compile (with `strictMode`, enabled in `build-profile.json5`) is:

- the **ArkTS-specific lint rules** beyond stock TypeScript (the `@ohos/hypium`
  frontend and the ArkTS static checker — e.g. the no-`any` / no-structural-typing
  enforcement), and
- that the `@noble/ed25519` named imports resolve under the **ArkTS module system**
  the same way they do under Node (they are declared in `oh-package.json5`;
  `@noble/*` ship as standard ES modules, consumed via `ohpm`).

Both are expected to pass — the code follows the documented ArkTS idioms (no `any`,
explicit types everywhere, classes over object-literal types, `ESObject` for
dynamic JSON) — but the authoritative check is the gated on-device build above.

## License

SPDX-License-Identifier: MIT
