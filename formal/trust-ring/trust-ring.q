/* Trust Ring queries */
EF (P_Attested = 1)                          /* Quorum reachable */
AG (P_Attested = 1 ⟹ P_Signatures >= 2)     /* Implies quorum reached */
EF (P_Revoked = 1)                            /* Revocation reachable */
AG (P_Revoked = 1 ⟹ P_Attested = 0)         /* Revocation removes attestation */
