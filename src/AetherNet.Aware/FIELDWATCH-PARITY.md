# Fieldwatch parity

What of Fieldwatch (<https://github.com/offgridpete/fieldwatch> at `cf6562d`) is in AetherNet.Aware, what is not, and
why. The ruler is Fieldwatch's own unit tests, ported with the same inputs and answers to
`tests/AetherNet.Aware.Tests`.

**Measured 2026-10-04:** 243 tests, 243 passing (`dotnet test tests/AetherNet.Aware.Tests`, net10.0). 168 are ported
from Fieldwatch's tests; 30 are ours, for ported code Fieldwatch has no test for; 45 are for Quiet help, which is ours
and not from Fieldwatch at all (its product half — the key, the message, both advert containers, the triggers, the
session and the guardians' side). Quiet help's service and app halves are tested in their own projects
(`AetherNetNodeService.Help.Tests`, `AetherNetNodeService.Ipc.Tests`, `AetherNetNodeService.Host.Tests`,
`AetherNet.Sample.Tests`). Deliberate breaks — the "moving with you" distance allowance, the AirTag-on-an-iPhone rule,
Quiet help's check on altered messages and its ignoring of older ones — each turned tests red before being undone.

## Code

| Fieldwatch (`app/src/main/java/app/fieldwatch/…`) | Here |
|---|---|
| `radio/BleAdParser.kt` — `parse`, `flagsLabel` | `BleAdParser.cs` |
| `radio/WifiIeParser.kt` — `parseIes` | `WifiIeParser.cs` |
| `domain/Models.kt` — signatures, sightings, `MacUtil`, `TextMatch`, `uuidAliases`, filters | `Signatures.cs`, `SignatureClass.cs`, `RadioKind.cs`, `Sighting.cs`, `MacUtil.cs`, `FilterEngine.cs` |
| `domain/RadioFacts.kt` | `RadioFacts.cs` |
| `domain/Rssi.kt` | `Rssi.cs` |
| `domain/SignatureExchange.kt` — the pack and `parse`/`parsePack` | `SignaturePack.cs` |
| `dist/fieldwatch-signatures-v2.json` | `Signatures/fieldwatch-signatures-v2.json` (unchanged) |
| `domain/SignatureEngine.kt` — `match` | `SignatureEngine.cs` |
| `domain/Geo.kt` — `Geo`, `CoTravel`, `TrackerMatch` | `Geo.cs`, `TrackerMatch.cs` |
| `FieldwatchApp.kt` — the operator path (`recordOperatorFix`, `acceptFix`) | `OperatorPath.cs` |
| `domain/OpenDroneId.kt` | `OpenDroneId.cs` |
| `domain/PayloadLocation.kt` | `PayloadLocation.cs` |
| `domain/SignatureFieldDecoder.kt` | `SignatureFieldDecoder.cs` |
| `domain/FastPair.kt` | `FastPair.cs` |
| `domain/Hunt.kt` | `Hunt.cs` |
| `domain/FilterEngine.kt` | `FilterEngine.cs` |
| `data/DeviceStore.kt` | `DeviceStore.cs` |

### Where it differs, and why

- **Malformed hex is skipped**, where Kotlin's `toInt(16)` would throw out of the decoder.
- **The decode cache is bounded** (1,024 parses per decode map, keyed by the map itself). Fieldwatch's grows for the
  session; AetherNetService runs for days.
- **Time comes from a `TimeProvider`**; `DeviceStore.Devices` and `Stats` are snapshots, not Kotlin flows.
- **No maker names** from addresses or company IDs: `Sighting.Vendor` stays empty (see `NOTICE.md`).
- **Presence spans are replaced, not edited in place**; what a reader sees is the same.
- **Unused parameters dropped**: `policy` on `match`/`refresh`, `staleSec` on `ingestBatch`. `match` takes `now`.
- **Names C# would not allow or would confuse**: `TrackerMatch.kind` → `KindOf` (its nested `Kind` enum →
  `TrackerKind`); `PayloadLocation.LAT/LON/OP_LAT/OP_LON` → `LatId/LonId/OpLatId/OpLonId` (they clash with the
  properties); top-level functions moved into `Uuids`, `DecodeNormalize`, `SightingDecode`, `SightingNotes`.
- **Name patterns** keep Java's meaning in .NET: `.` does not match a line break and the whole name must match.
- **Numbers print the same in every culture** (`.` decimals, ASCII minus).
- **A 32-bit float field's raw key** prints the .NET way ("1" not Java's "1.0"). It only matters to an enum keyed on a
  float field; the stock pack has no float fields.
- **The "not a pack" message** drops Fieldwatch's pointer to its own Settings screen.

### Not ported

| Fieldwatch | Why |
|---|---|
| `BleAdParser.facts`, `WifiIeParser.parse`, `BleRadio`, `WifiRadio`, `ScanService`, `Permissions` | Android scanning: the service's job (next step) |
| `SignatureExchange` export, merge, stock overlay; `SignatureEngine.suggestFleet`, `SignatureCandidates` | Packs and a person's own signatures come with signature packs over the mesh (plan phase 6) |
| `DeviceExplain`, `AdvPayloadDecoder`, `CodDecoder`, `RadioDb`, `OuiLookup`, `FastPairModels` | Built on Bluetooth SIG tables (terms not yet checked) or names outside Fieldwatch's MIT grant — `NOTICE.md` |
| Watchlist, bookmarks, alerts, arrivals | Not in the plan's first version; the app decides how a warning is shown |
| Sits, debriefs, AI export, ATAK/CoT, PDF, map plots, logs, `AircraftTrail` | Out of scope in the plan |
| `CatalogRemote`, `PlaceLookup` | A central download and Google's geocoder |
| UI helpers (list titles, speech labels, palette, on-screen masking, `uuidShortOrFull`, map cells) | The app's job |

## Tests

| Fieldwatch test file | Tests | Ported | Here |
|---|---|---|---|
| `radio/BleAdParserTest.kt` | 11 | 11 | `BleAdParserTests` |
| `radio/WifiIeParserTest.kt` | 10 | 10 | `WifiIeParserTests` |
| `domain/RadioFactsTest.kt` | 5 | 5 | `RadioFactsTests` |
| `domain/HuntTest.kt` | 6 | 6 | `HuntTests` |
| `domain/OpenDroneIdTest.kt` | 4 | 4 | `OpenDroneIdTests` |
| `domain/FilterEngineTest.kt` | 9 | 9 | `FilterEngineTests` |
| `data/DeviceStoreTest.kt` | 12 | 12 | `DeviceStoreTests` |
| `domain/DefaultCatalogTest.kt` | 15 | 15 | `StockCatalogTests` |
| `domain/StockCatalogFileTest.kt` | 3 | 2 | `SignaturePackTests` |
| `domain/SignatureExchangeTest.kt` | 58 | 47 | `SignatureMatchTests` (43), `SignaturePackTests` (3), `FilterEngineTests` (1) |
| `domain/SignatureFieldDecoderTest.kt` | 49 | 46 | `SignatureFieldDecoderTests` (45), `SignaturePackTests` (1) |
| `domain/DeviceExplainTest.kt` | 5 | 1 | `SmallPiecesTests.RssiNotAvailable` |
| **Total** | **187** | **168** | |

**Not ported (19):**

- `StockCatalogFileTest.legacyPackStaysOnCatalog77` — the older `dist/fieldwatch-signatures.json` is not shipped.
- `SignatureExchangeTest`: `stockPackRoundTripsAndImportsAsNoOp`, `overlayStockReplacesAttentionAndClassKeepsMuteAndExtraRules`,
  `overlayStockKeepsLocalDecodeWhenPackSkippedIt`, `overlayStockDoesNotClobberCustomWithSameId`,
  `customSignatureAddsAndNameCollisionRenames`, `importedBodywornFoldsIntoWearables`, `extraRuleOnStockRowMerges`,
  `sameRulesDifferentIdAreSkipped`, `overlayStockDropsRetiredUnknownSignature` (export, import, merge);
  `createFromDeviceSkipsDirectGlob`, `createFromDeviceKeepsProductGlob` (`suggestFleet`).
- `SignatureFieldDecoderTest`: `packRoundTripKeepsIncludeCompanyId`, `packRoundTripKeepsDecode` (export),
  `importBackupAppliesIncomingDecodeOnSameId` (import).
- `DeviceExplainTest`: the four tests of `DeviceExplain` itself.

**Assertions left out of ported tests:** `FilterPreset.isBuiltIn()` (Fieldwatch's saved-config migration); every
`DefaultCatalog.defaultWatchlist()` check (no watchlist here); `DeviceDetailText` and `DeviceExplain` text; and, in
`distPackMatchesDefaultCatalog`, the comparison with `DefaultCatalog.fleets()` — Fieldwatch's Kotlin catalog is not
ported because its pack, which that test proves equal to it, is.

**Built differently, same answers:** the two parse tests and the live-flag test read the stock pack's own JSON where
Fieldwatch first exported a pack; `matcherStillHitsAfterImportAndRespectsRadio` appends its custom signature where
Fieldwatch imported it; `DeviceStoreTest` runs on a hand-moved clock.

**Ours (30):** `CoTravelTests` (9), `GeoTests` (6), `OperatorPathTests` (5), `DeviceStoreLifetimeTests` (5),
`SmallPiecesTests` (4: tracker kinds, a carried iPhone, signatures that need several radios, the folded class) and
`SignaturePackTests.StockPackIsFieldwatchCatalog90` (1).
