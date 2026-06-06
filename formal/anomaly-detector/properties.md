# Anomaly Detector — Properties

## P1 — No False Negatives
After 3 attack samples observed, T_RaiseAlert is enabled and reachable. ✓

## P2 — No False Positives
T_RaiseAlert requires `P_SignatureSamples ≥ 3`. Without attack observations,
P_SignatureSamples = 0 and alert is impossible. ✓
