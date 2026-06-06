/* SOS Flood queries */
EF (P_N1_Alerted = 1 AND P_N2_Alerted = 1 AND P_N3_Alerted = 1)  /* Coverage */
AG (P_TTL >= 0)                                                   /* TTL bounded */
EF (P_TTL = 0)                                                    /* Termination */
