# Forge Eviction — Properties

## P1 — Cache Bounded
Sum of cached + free = 2 (capacity). Each cache transition exchanges
1 free for 1 cached. Each evict transition replaces. Total conserved. ✓

## P2 — No Starvation
Pkg1 is evictable via T_CachePkg3_EvictsPkg1 — Pkg1 is not held forever
once a new arrival displaces it. ✓
