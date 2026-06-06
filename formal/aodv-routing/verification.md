## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 12 |
| Transitions | 8 |
| **Reachable states** | **20** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_A_HasRouteToC_viaB + P_A_NoRouteToC = 1` holds in **all** reachable states
- `P_B_HasRouteToC_direct + P_B_NoRouteToC = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_RREP_CtoB | 2 |
| P_A_HasRouteToC_viaB | 1 |
| P_A_NoRouteToC | 1 |
| P_A_RREQId_Available | 1 |
| P_B_DedupSeen_RREQ | 1 |
| P_B_HasRouteToC_direct | 1 |
| P_B_NoRouteToC | 1 |
| P_FreshSeqNum_C | 1 |
| P_RREP_BtoA | 1 |
| P_RREQ_AtoB | 1 |
| P_RREQ_BtoC | 1 |
| P_StaleSeqNum_C | 1 |
