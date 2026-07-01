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
| 1 | `EF (P_D1_HasKey = 1 AND P_D2_HasKey = 1 AND P_D3_HasKey =...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Sync_to_D2 | 31 |
| P_Sync_to_D3 | 31 |
| P_D2_HasKey | 15 |
| P_D3_HasKey | 15 |
| P_D1_HasKey | 1 |
