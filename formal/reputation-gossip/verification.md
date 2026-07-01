## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 3 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_N1_KnowsScore = 1 AND P_N2_KnowsScore = 1 AND P_N3_...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Gossip12 | 39 |
| P_Gossip23 | 19 |
| P_N2_KnowsScore | 19 |
| P_N3_KnowsScore | 12 |
| P_N1_KnowsScore | 1 |
