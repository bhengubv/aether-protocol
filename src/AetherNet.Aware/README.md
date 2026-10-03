# AetherNet.Aware

What a device's radios can hear around it, a finder tag that is moving with you, and Quiet help. The listening only
listens: it never transmits, joins, pairs or connects, and nothing it hears leaves the device. Quiet help is the one
part that sends, and only when the person asks.

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
| `HelpKey`, `HelpCodec`, `HelpSession`, `HelpWatch` | Quiet help: a person asks the guardians they chose for help without a sound and shares their trail until they are safe; the guardians' phones read it, keep the trail, and Find it leads them in |

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

## Quiet help

The person's phone runs a `HelpSession` (help, or a walk) started by the person, and sends `NextMessage()` — 23 bytes
sealed with their `HelpKey` — as a Bluetooth advert and over the mesh to each guardian. A guardian's `HelpWatch`
knows the people who chose them (`Watch`), reads their messages (`Hear`), and keeps each one's trail and loudness
until a day after the person is safe. Anyone else's phone cannot read the message and keeps nothing. The safeguards
and what the service and Aether add are in `docs/aether-aware-plan.md`, "Quiet help and trails".

## Limits

- Nothing guarantees a tracker is found: one that is off, asleep, quiet, cellular-only or rotating its address fast
  may not show.
- A radio is keyed by its address. A device that rotates its address appears as a new radio each time; the old one is
  forgotten after 3 minutes without a signature, 15 with one.
- No maker names from addresses yet (see `NOTICE.md`).
