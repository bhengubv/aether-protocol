# ChipIn — Properties

## P1 — Conservation Invariant
`Sum(C1, C2, Pool, Creator) = 100` for every reachable marking.
Each transition exchanges tokens with arc-weight equality:
- T_Ci: -50 from Ci, +50 to Pool. Net 0.
- T_GoalReached: -100 from Pool, +100 to Creator. Net 0.
Initial sum = 100. Conserved. ✓

## P2 — Goal Atomicity
T_GoalReached requires 100 in Pool (arc weight). Either fires fully or
doesn't fire — Petri net atomic firing rule. No partial debit. ✓

## P3 — Reachability of Goal
Witness: T_C1 → T_C2 → T_GoalReached → M_goal. ✓
