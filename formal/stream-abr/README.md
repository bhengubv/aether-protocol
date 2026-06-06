# Stream Segment Delivery + ABR

## What This Proves

`AdaptiveBitrateController` selects bitrate rungs based on bandwidth.
This model proves no segment is lost during rung transitions and ABR
converges to the highest sustainable rung.

| Property | Status |
|---|---|
| Bounded buffer (no overflow) | ✅ |
| Rung selection monotonic on bandwidth direction | ✅ |
| No segment loss during rung change | ✅ |

## Files

`stream-abr.pnml` | `.q` | `properties.md` | `state-space.md`
