# Health — Properties

## P1 — Eventual Healthy
Witness: T_RoutingHeals → T_DTNHeals → T_OverallReady → Overall_Healthy. ✓

## P2 — Stability
T_OverallReady uses test arcs on healthy components. No transition
consumes P_Overall_Healthy. Once reached, stays. ✓
