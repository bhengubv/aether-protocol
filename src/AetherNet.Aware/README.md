# AetherNet.Aware

What a device's radios can hear around it, and a finder tag that is moving with you. Listening only: nothing here
transmits, joins, pairs or connects, and nothing leaves the device.

Platform-neutral C#. The host that owns the radios (AetherNetService) feeds it Bluetooth adverts, Wi-Fi beacons and
its own position; this library reads them, names them, and keeps what was heard. Ported from
[Fieldwatch](https://github.com/offgridpete/fieldwatch) (MIT, Off Grid Pete LLC) — see `NOTICE.md` and
`FIELDWATCH-PARITY.md`.

## What is in it

| Piece | What it does |
|---|---|
| `BleAdParser`, `WifiIeParser` | Read a Bluetooth advert and a Wi-Fi beacon's elements into fields |
| `StockSignatures`, `SignatureExchange` | The stock signature pack (252 signatures, 6,981 rules) and the pack reader |
| `SignatureEngine` | Which signatures a radio matches: finder tags, drones, cameras, glasses, routers… |
| `DeviceStore` | Keeps what was heard: merges adverts per radio, labels, marks gone, forgets |
| `OperatorPath`, `CoTravel` | This device's walk, and whether a Bluetooth radio has moved with it |
| `TrackerMatch` | Finder tag, beacon or wearable |
| `OpenDroneId`, `SignatureFieldDecoder`, `PayloadLocation` | Drone Remote ID (position, ID, pilot) and other decoded fields |
| `Hunt` | "Find it": closer / further cues and a ticking interval from loudness |
| `FilterEngine` | Which radios to show |
| `FastPair` | Pairing-mode Fast Pair adverts vs. account-key background noise |

## Use

```csharp
var store = new DeviceStore();
var path = new OperatorPath();
var fleets = StockSignatures.Fleets;

// Each time the radios hear something (stamped with this device's last position, if known):
store.IngestBatch(observations, fleets);

// Each time the platform gives a position fix:
path.Accept(lat, lon, fixAtUnixMs, accuracyM);

// Every second or so:
store.Refresh(fleets, staleSec: 30);
var travel = CoTravel.Ctx.Of(path.Copy());
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var names = fleets.ToDictionary(f => f.Id, f => f.Name);
var withYou = store.Devices
    .Where(d => CoTravel.WithYou(d, travel, now) && TrackerMatch.IsTracker(d, names))
    .ToList();
```

## Limits

- Nothing guarantees a tracker is found: one that is off, asleep, quiet, cellular-only or rotating its address fast
  may not show.
- A radio is keyed by its address. A device that rotates its address appears as a new radio each time; the old one is
  forgotten after 3 minutes without a signature, 15 with one.
- No maker names from addresses yet (see `NOTICE.md`).
