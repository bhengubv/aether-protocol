## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 6 |
| Transitions | 4 |
| **Reachable states** | **12** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_A_RouteTable = 1) EF (P_RejectedAttacks = 2) AG (P_...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 3` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_RejectedAttacks | 2 |
| P_A_RouteTable | 1 |
| P_FreshSeqAvailable | 1 |
| P_HonestRREP | 1 |
| P_MaliciousRREP_Stale | 1 |
| P_MaliciousRREP_Unsigned | 1 |
