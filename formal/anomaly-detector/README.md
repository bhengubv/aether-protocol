# Anomaly Detector — No False Negatives

## What This Proves

`IBehavioralAnomalyMeshDetector` flags traffic patterns matching attack
signatures. This model proves the detector **always flags** matching
patterns in bounded firings (no false negatives).

| Property | Status |
|---|---|
| Matching pattern always reaches Flagged | ✅ |
| No flagging without matching pattern | ✅ |

## Files

- `anomaly-detector.pnml` | `.q` | `properties.md` | `state-space.md`
