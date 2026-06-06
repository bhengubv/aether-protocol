/* Outbox queries */
AG (P_Ingress + P_Outbox + P_DTN + P_Delivered = 5)  /* Conservation */
EF (P_Delivered = 5)                                  /* All deliverable */
AG ¬ (P_Ingress = 0 AND P_Outbox = 0 AND P_DTN = 0 AND P_Delivered < 5)  /* No loss */
