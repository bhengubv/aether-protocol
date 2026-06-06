/* AetherNet Vault Erasure Coding — K-of-N reachability queries
   Run: verifytapn --trace some vault-erasure.pnml --query vault-erasure.q
   All queries expected: SATISFIED
   SPDX-License-Identifier: MIT */

/* Q1 — Initial recoverability: from the initial state, P_Recovered is reachable. */
EF (P_Recovered = 1)

/* Q2 — Single-failure tolerance: from EVERY single-failure state,
   P_Recovered remains reachable. */
AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 >= 2) => EF (P_Recovered = 1))

/* Q3 — Two-failure unrecoverability: from every state with fewer than K=2
   shards alive, P_Recovered is NOT reachable. */
AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 < 2) => ¬ EF (P_Recovered = 1))

/* Q4 — Self-healing from every single-failure state to full redundancy. */
AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 = 2) =>
     EF (P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 = 3))

/* Q5 — Conservation invariant: each node is in exactly one shard state. */
AG (P_Has_Shard_1 + P_No_Shard_1 = 1 AND
    P_Has_Shard_2 + P_No_Shard_2 = 1 AND
    P_Has_Shard_3 + P_No_Shard_3 = 1)

/* Q6 — Permanent loss is reachable (after the threshold is crossed). */
EF (P_Lost = 1)

/* Q7 — Loss is not reachable without first dropping below K. */
AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 >= 2) =>
     ¬ (P_Lost = 1))
