## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 3 |
| **Reachable states** | **2000** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_AtNode2 = 1) EF (P_Expired = 1)` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_AtNode2 | 999 |
| P_AtNode1 | 1 |
| P_Drop | 1 |
| P_Expired | 1 |
