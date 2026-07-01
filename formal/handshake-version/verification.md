## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 2 |
| **Reachable states** | **4** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_AgreedV2 = 1) EF (P_NoCommonVersion = 1) AG (P_Agre...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_A_v1 + P_NoCommonVersion = 1` holds in **all** reachable states
- `P_A_v2 + P_AgreedV2 = 1` holds in **all** reachable states
- `P_AgreedV2 + P_B_v2 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_A_v1 | 1 |
| P_A_v2 | 1 |
| P_AgreedV2 | 1 |
| P_B_v2 | 1 |
| P_NoCommonVersion | 1 |
