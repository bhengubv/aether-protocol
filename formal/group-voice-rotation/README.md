# Group Voice — Key Rotation Under Churn

## What This Proves

`IGroupVoiceCallMeshService` rotates the group key when a member
leaves or joins. This model proves forward secrecy is preserved
under member churn: after rotation, the old key cannot reach
the new chain.

| Property | Status |
|---|---|
| Key rotation triggered by member event | ✅ |
| Old key destroyed on rotation | ✅ |
| Member who left cannot derive new key | ✅ |

## Files

- `group-voice-rotation.pnml` | `.q` | `properties.md` | `state-space.md`
