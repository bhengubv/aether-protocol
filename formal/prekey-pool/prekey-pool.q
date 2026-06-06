/* Pre-Key Pool queries */

/* Q1 — No exhaustion: pool never goes below 1 */
AG (P_Pool >= 1)

/* Q2 — Refill reachable from any low state */
AG (P_Pool = 1 ⟹ EF (P_Pool >= 3))

/* Q3 — Pool capped (no unbounded refill) */
AG (P_Pool <= 7)
