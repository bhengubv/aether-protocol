# SOS Flood — Properties

## P1 — Coverage
**Statement:** Every node reaches Alerted state.
**Witness:** T_N1 → T_N2 → T_N3 — all 3 alerted. ✓

## P2 — Termination
**Statement:** TTL eventually exhausts; no infinite firings.
**Proof:** Each forwarding transition consumes 1 TTL token. Initial TTL=3,
maximum forwarding firings = 3. ✓

## P3 — No Re-Flooding
Each `T_Ni_*` consumes `P_SosToNi`. There's no transition that re-produces
the same place's token, so each node processes the alert at most once. ✓
