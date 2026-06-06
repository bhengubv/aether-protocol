## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 8 |
| Transitions | 5 |
| **Reachable states** | **10000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_ChainKey_E2 + P_FreshDH_E2 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Attacker_E0 | 39 |
| P_Attacker_E1 | 37 |
| P_Attacker_E2 | 36 |
| P_ChainKey_E0 | 1 |
| P_ChainKey_E1 | 1 |
| P_ChainKey_E2 | 1 |
| P_FreshDH_E1 | 1 |
| P_FreshDH_E2 | 1 |
