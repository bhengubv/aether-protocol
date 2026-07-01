## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 10 |
| Transitions | 5 |
| **Reachable states** | **6** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_A_Established + P_A_Rejected = 0 ⟹ EF (P_A_Establis...` | ✅ SAT |
| 2 | `EF (P_A_Established = 1 AND P_B_Established = 1) EF (P_A_...` | ✅ SAT |
| 3 | `AG ¬ (P_A_Established = 1 AND P_B_Rejected = 1) AG ¬ (P_A...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_A_Established | 1 |
| P_A_HelloSent | 1 |
| P_A_Idle | 1 |
| P_A_Rejected | 1 |
| P_AckInFlight | 1 |
| P_B_Established | 1 |
| P_B_Idle | 1 |
| P_B_Rejected | 1 |
| P_HelloInFlight | 1 |
| P_NegAckInFlight | 1 |
