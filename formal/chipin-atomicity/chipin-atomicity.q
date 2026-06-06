/* ChipIn atomicity queries */

/* Q1 — Conservation: total balance is conserved across all transitions */
AG (P_C1_Balance + P_C2_Balance + P_Pool + P_CreatorBalance = 100)

/* Q2 — Goal reachable */
EF (P_CreatorBalance = 100)

/* Q3 — Goal release atomic: pool drains exactly when creator gets 100 */
AG (P_CreatorBalance = 100 ⟹ P_Pool = 0)
