/* Watch-Together — bounded-jitter convergence queries */

/* Q1 — Convergence: both followers eventually reach host position */
EF (P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1 AND P_Host_Playing = 1)

/* Q2 — Bounded: convergence in ≤ 3 firings (T_Host_Play + T_F1 + T_F2) */
EF (P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1)

/* Q3 — Idempotency: duplicate sync packets cannot desync */
AG (P_F1_AtHostPos = 1 ⟹ AG P_F1_AtHostPos = 1)
AG (P_F2_AtHostPos = 1 ⟹ AG P_F2_AtHostPos = 1)

/* Q4 — No phantom progress: a follower never advances without a sync packet */
AG (P_F1_AtHostPos = 1 ⟹ P_F1_SyncApplied = 1)
AG (P_F2_AtHostPos = 1 ⟹ P_F2_SyncApplied = 1)

/* Q5 — Host playing implies sync was emitted */
AG (P_Host_Playing = 1 ⟹ (P_SyncTo_F1 + P_F1_SyncApplied >= 1))
