## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 4 |
| **Reachable states** | **2000** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Cached = 1) AG (P_Cached = 1 => P_HashVerified = 1)...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Fetched | 24 |
| P_HashVerified | 11 |
| P_TamperDetected | 11 |
| P_Cached | 7 |
| P_OriginalPkg | 1 |
