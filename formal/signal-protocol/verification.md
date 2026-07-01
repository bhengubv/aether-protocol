## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 8 |
| Transitions | 5 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Attacker_E0 <= 1)` | ❌ NOT SAT |
| 2 | `EF (P_Attacker_E0 = 1 AND P_ChainKey_E1 = 1 AND P_Attacke...` | ✅ SAT |
| 3 | `EF (P_ChainKey_E2 = 1)` | ✅ SAT |
| 4 | `AG (P_ChainKey_E0 + P_ChainKey_E1 + P_ChainKey_E2 <= 1)` | ✅ SAT |
| 5 | `EF (P_Attacker_E0 = 0 AND P_Attacker_E1 = 1 AND P_Attacke...` | ✅ SAT |
| 6 | `EF (P_Attacker_E0 = 1 AND P_Attacker_E1 = 1 AND P_Attacke...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_ChainKey_E2 + P_FreshDH_E2 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Attacker_E0 | 22 |
| P_Attacker_E1 | 20 |
| P_Attacker_E2 | 19 |
| P_ChainKey_E0 | 1 |
| P_ChainKey_E1 | 1 |
| P_ChainKey_E2 | 1 |
| P_FreshDH_E1 | 1 |
| P_FreshDH_E2 | 1 |
