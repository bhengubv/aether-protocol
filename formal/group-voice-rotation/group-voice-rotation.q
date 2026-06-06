/* Group key rotation queries */
EF (P_GroupKey_v2 = 1 AND P_GroupKey_v1 = 0)  /* Rotation reachable */
AG (P_LeftMember_HasOldKey = 1 AND P_GroupKey_v2 = 1
    ⟹ P_GroupKey_v1 = 0)                       /* Left member cannot have v2 */
