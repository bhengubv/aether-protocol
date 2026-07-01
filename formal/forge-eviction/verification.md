## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 4 |
| **Reachable states** | **10** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Pkg1_Cached + P_Pkg2_Cached + P_Pkg3_Cached + P_Cac...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 2` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_CacheSlotsFree | 2 |
| P_Pkg1_Cached | 2 |
| P_Pkg2_Cached | 2 |
| P_Pkg3_Cached | 2 |
