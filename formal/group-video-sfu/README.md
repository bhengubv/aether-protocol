# Group Video — FullMesh → SFU Switchover

## What This Proves

`IGroupVideoMeshService` switches from FullMesh (all-to-all) to SFU
(forwarding via one node) when participant count crosses
`SfuThresholdParticipants`. Proves switchover is atomic and no video
frame is lost.

| Property | Status |
|---|---|
| Mode mutually exclusive (FullMesh XOR SFU) | ✅ |
| Switchover triggers exactly once per threshold cross | ✅ |
| No frame loss during switch | ✅ |

## Files

`group-video-sfu.pnml` | `.q` | `properties.md` | `state-space.md`
