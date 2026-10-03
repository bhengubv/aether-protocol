# Aether Aware — plan

**Status:** plan only. Nothing is built. Written 2026-10-03.
**Source:** [github.com/offgridpete/fieldwatch](https://github.com/offgridpete/fieldwatch) — Fieldwatch, MIT, by Off Grid Pete LLC.

## What it is

Aether Aware tells a person what their device's radios can hear around them — and warns them when something is
following them. It only listens: it never transmits, joins, pairs or connects. It needs no account and no server.

What a person gets:

- **Moving with you** — a finder tag (AirTag / Find My, SmartTag, Tile, Pebblebee, Google Find Hub, DULT tags) that
  has stayed with them while they moved. The safety feature: someone slipping a tag into a bag.
- **Drones** — drones overhead that broadcast Remote ID, with what they say about themselves (where they are, their
  ID, where the pilot is when sent).
- **Around you** — the Wi-Fi access points and Bluetooth devices in range, named where they can be (cameras, glasses,
  earbuds, routers), filtered so it is not a wall of noise.
- **Find it** — walk towards one device with a ticking sound that speeds up as it gets louder (like a Geiger counter).

## What Fieldwatch is, and what we take

Fieldwatch is a Kotlin Android app (MIT) that does exactly the listening above, offline. About 2,000 stars, started
2026-09-15, actively changed.

**Take (port to C#):**

| Fieldwatch | What it does |
|---|---|
| `radio/BleAdParser.kt`, `radio/WifiIeParser.kt` | Read a Bluetooth advert and a Wi-Fi beacon into fields |
| `domain/SignatureEngine.kt`, `SignatureFieldDecoder.kt`, `DefaultCatalog.kt` | Match what was heard against a signature pack |
| `dist/fieldwatch-signatures-v2.json` | The pack: 252 groups, ~7,000 rules (OUI, name, service UUID, manufacturer ID/data, vendor IE) |
| `CoTravel` in `domain/Geo.kt` | "Moving with you": BLE only; the phone moved ≥45 m; the device heard within 90 s, loud (≥ −75 dBm) for two-thirds of its trail, and last heard within max(50 m, 15 s at current speed) + 25 m of where the phone is |
| `domain/OpenDroneId.kt` | Drone Remote ID (ASTM F3411) from Bluetooth and Wi-Fi |
| `domain/FastPair.kt`, `FastPairModels.kt` | Name Fast Pair earbuds and accessories |
| `domain/Hunt.kt` | Loudness → tick interval for "Find it" |
| `domain/DeviceExplain.kt`, `FilterEngine.kt` | Plain-words explanation of a device; filters |
| Their unit tests | Our parity ruler (see Phases) |

**Leave:**

- The signature pack fetched from GitHub (`CatalogRemote.kt`) — a central download. Ours travels the mesh.
- Place names from the system geocoder (`PlaceLookup.kt`) — Google's on most phones.
- "AI Export", ATAK/CoT publishing, PDF debriefs — not needed for the first versions; can come later.

## Where it lives

The same split as everything else: AetherNetService owns the radios; Aether shows and manages.

- **`src/AetherNet.Aware`** (new, platform-neutral C#) — parsers, signature pack and engine, "moving with you", drone
  decoding, Fast Pair names, find-it ticks, and the aggregator that keeps what was heard. Ported files keep Fieldwatch's
  copyright line.
- **AetherNetService, Android** (`AetherNet.Transport.Android`) — one more listener beside the mesh radios: a Bluetooth
  scan for every advert (not only AetherNet's), Wi-Fi scan results, and location samples from the phone's own GPS
  (`LocationManager`, never Google Play services).
- **AetherNetService, Windows** (`AetherNet.Transport.Windows`) — a passive Bluetooth watcher and the Wi-Fi network list.
  No "moving with you" on a computer without location.
- **The node contract** — `GetAwareAsync` (what is around, what is moving with you, drones) and a push when that
  changes, carried by the binder and the pipe like every other call. Plus the Aether Aware switch.
- **Aether** — an Aware screen (Moving with you · Drones · Around you · Find it), and its switch and permission rows in
  Settings with what on and off each mean.

## Decisions for you

1. **Location for AetherNetService.** "Moving with you" needs the phone's position, and Android hides some Bluetooth
   adverts from a scanner that promises not to use location (`neverForLocation`, which AetherNetService declares today).
   So AetherNetService would ask for Location. *Recommend:* yes — one more row in Aether's Settings, "Location — spot
   trackers moving with you". Denied, Aware still shows what is around and drones; only "moving with you" waits.
   *Must be measured first* (Phase 3): whether dropping `neverForLocation` makes the mesh's own Bluetooth need Location
   on the Pixel (Android 16). The P30 (Android 10) already needs Location for Bluetooth.
2. **The warning.** A tag moving with you is a safety warning. *Recommend:* AetherNetService posts it as a notification
   (it already has one, for staying up); tapping it opens Aether's Aware screen.
3. **On by default.** *Recommend:* yes, with plain words for on and off, and a listening-strength choice
   (strong / balanced / light) defaulting to balanced, because listening to everything costs battery.
4. **Vendor names.** Fieldwatch's lookup tables carry IEEE and Bluetooth SIG lists under those bodies' own terms.
   *Recommend:* check those terms before shipping names; until then show classes ("a finder tag", "a drone",
   "a camera") without vendor names.
5. **Signature packs.** *Recommend:* the stock pack ships inside AetherNetService; updates come as a signed card from
   the makers' AetherTag over the mesh (`AetherNet.Cards`); a person can import/export a pack in Fieldwatch's file
   format; their own additions stay on the device.
6. **Test hardware.** Proving "moving with you" needs a finder tag (an AirTag, SmartTag or Tile) carried on a walk.
   Fast Pair can use the Redmi Buds already paired to the dev PC. Drones need a drone broadcasting Remote ID (optional).
7. **Telling your Circle** about a tag or a drone you have seen. *Recommend:* later, opt-in, never with location unless
   the person chooses to share it.

## Phases

Sizes as on the work tracker: S / M / L.

1. **The ruler first (M).** Port Fieldwatch's unit tests — advert and beacon parsing, Remote ID, signature decoding,
   filters and "moving with you", find-it ticks, pack loading — into C# tests with the same inputs and answers.
   *Done when:* they exist, compile, and fail for want of code.
2. **AetherNet.Aware (L).** The C# port. *Done when:* every ported test passes; the aggregator has its own tests
   (devices expire, rotating addresses do not count as many devices, a tag left behind stops being "with you").
3. **Listening in AetherNetService on Android (L).** First the Location measurement in decision 1, on the P30 and the
   Pixel. Then the listener: a Bluetooth scan for every advert (extended adverts too, and a filter list so Android keeps
   it running with the screen off), Wi-Fi results within Android's scan limits (at most every 30 s), GPS samples; the
   contract calls and push; what was heard kept on the device and forgotten after a set time. *Done when:* on the P30
   and the Pixel, Aware lists real access points and adverts; a tag carried on a walk shows "moving with you"; the
   mesh still works with Location allowed and denied.
4. **Aether (M).** The Aware screen and the Settings rows, light and dark; the notification opens the screen.
   *Done when:* bUnit tests pass, and the screens are seen on the P30 in both themes.
5. **Windows (M).** The passive Bluetooth watcher and the Wi-Fi list in the Windows service. *Done when:* unit tests and
   the build pass. Nothing is run on the dev PC unless you ask.
6. **Packs over the mesh, and the Circle (M).** Signed signature packs fetched from peers; import/export; the opt-in
   Circle warning. *Done when:* a pack published from one phone reaches another over the mesh, and a tampered pack is
   refused.

## Limits, said plainly

- Nothing guarantees a tracker is found: a device that is off, asleep, quiet, cellular-only or rotating its address
  fast may not show — Fieldwatch says the same.
- Android limits Wi-Fi scans (four in two minutes for an app in front; far fewer for one in the background — to be
  measured with AetherNetService's foreground service) and pauses unfiltered Bluetooth scans with the screen off.
- Listening to everything costs battery; the strength choice is the person's.
- EMUI (the P30) can still kill AetherNetService, as today; Aware stops while it is dead.
- What is heard includes other people's device addresses. It stays on the device, is forgotten after a set time, and
  is never uploaded.
- In Settings, "Aether Aware" sits near the "Wi-Fi Aware" radio row. They are different things; the words say so.

## Not in it

Wi-Fi monitor mode or client probes, Bluetooth Classic, cellular, direction finding — none of which Fieldwatch does
either — and, for now, AI export, ATAK/CoT and PDF debriefs.

## Licence

Fieldwatch's code is MIT: ported files keep `Copyright (c) 2026 Off Grid Pete LLC` and the MIT notice, and `NOTICE`
names Fieldwatch. Its IEEE and Bluetooth SIG tables are not MIT — see decision 4.
