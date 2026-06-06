## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 11 |
| Transitions | 7 |
| **Reachable states** | **10000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_W1_Available + P_W1_Vouched = 1` holds in **all** reachable states
- `P_W2_Available + P_W2_Vouched = 1` holds in **all** reachable states
- `P_W3_Available + P_W3_Vouched = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_S_FraudFlagged | 21 |
| P_W1_Penalty | 18 |
| P_W2_Penalty | 18 |
| P_W3_Penalty | 18 |
| P_S_Count | 3 |
| P_W1_Available | 1 |
| P_W1_Vouched | 1 |
| P_W2_Available | 1 |
| P_W2_Vouched | 1 |
| P_W3_Available | 1 |
| P_W3_Vouched | 1 |
