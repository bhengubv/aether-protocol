## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 6 |
| Transitions | 6 |
| **Reachable states** | **6** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Source + P_Relay + P_Delivered + P_Expired = 1)` | ✅ SAT |
| 2 | `AG (EX true)` | ❌ NOT SAT |
| 3 | `EF (P_Delivered = 1)` | ✅ SAT |
| 4 | `AG ((P_Source = 1 AND P_RelayDown = 1) => EF (P_Delivered...` | ✅ SAT |
| 5 | `EF (P_Expired = 1)` | ✅ SAT |
| 6 | `AG (P_RelayUp + P_RelayDown = 1)` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 2` holds in **all** reachable states
- `P_RelayDown + P_RelayUp = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Delivered | 1 |
| P_Expired | 1 |
| P_Relay | 1 |
| P_RelayDown | 1 |
| P_RelayUp | 1 |
| P_Source | 1 |
