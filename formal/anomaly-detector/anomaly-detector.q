/* Anomaly queries */
EF (P_Flagged = 1)                                          /* Can be flagged */
AG (P_TrafficAttack = 0 AND P_SignatureSamples >= 3
    ⟹ EF P_Flagged = 1)                                    /* No false negative */
AG (P_Flagged = 1 ⟹ EF (P_TrafficAttack <= 3))            /* Flagging implies attack observed */
