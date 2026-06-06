/* TAPAAL TCTL queries for Watch-Together Timed
   Run: verifytapn watch-together.tpn --query watch-together.q */

/* Q1 — Bounded convergence: from host-playing, both followers
   reach host position within 100ms */
EF[<=100] (P_Host_Playing = 1 AND P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1)

/* Q2 — Universal convergence: every path from host-playing leads
   to both-synced within 100ms */
AG (P_Host_Playing = 1 IMPLY AF[<=100] (P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1))

/* Q3 — Maximum jitter: in no reachable state is one follower synced
   for more than 50ms while the other isn't */
AG NOT (P_F1_AtHostPos = 1 AND P_SyncToF2 = 1 AND clock(global) > 50)
