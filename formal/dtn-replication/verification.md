## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 5 |
| **Reachable states** | **8** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Delivered >= 1) EF (P_Replicas_Geohash >= 3)` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Delivered | 3 |
| P_Replicas_Geohash | 3 |
| P_Bundle | 1 |
| P_Custody_N1 | 1 |
| P_Custody_N2 | 1 |
