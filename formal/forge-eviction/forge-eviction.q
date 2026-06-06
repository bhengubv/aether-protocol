/* Forge eviction queries */
AG (P_Pkg1_Cached + P_Pkg2_Cached + P_Pkg3_Cached + P_CacheSlotsFree = 2)  /* Bounded */
EF (P_Pkg1_Cached = 0 AND P_Pkg3_Cached = 1)  /* Eviction reachable */
