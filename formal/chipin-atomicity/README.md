# ChipIn — Financial Atomicity

## What This Proves

`IWatchTogetherMeshService.StartChipInAsync` lets viewers pool small
contributions toward a creator goal. This model proves financial
atomicity:

| Property | Status |
|---|---|
| Sum conservation: total debited = total credited | ✅ Proved |
| No partial charge (no debit without credit) | ✅ Proved |
| Goal reachable only via legitimate contributions | ✅ Proved |

## Scenario

2 contributors (C1, C2) each pledge 50 ZAR. Goal = 100. On goal-reached,
funds release atomically to creator.

## Files

- `chipin-atomicity.pnml` | `.q` | `properties.md` | `state-space.md`
