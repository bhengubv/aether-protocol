# Reputation Gossip — Properties

## P1 — Convergence
EF all-3-know reachable via T_N1_Gossip → T_N2_MergeAndGossip → T_N3_Merge. ✓

## P2 — Idempotent
Each Ni_KnowsScore is bounded at 1 (no transition multiplies).
N1's gossip transition test-arc preserves its own knowledge — sending
doesn't lose it. ✓
