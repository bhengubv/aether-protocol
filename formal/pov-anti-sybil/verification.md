## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 11 |
| Transitions | 7 |
| **Reachable states** | **2001** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_W1_Vouched <= 1 AND P_W2_Vouched <= 1 AND P_W3_Vouc...` | ✅ SAT |
| 2 | `AG (P_S_Count = P_W1_Vouched + P_W2_Vouched + P_W3_Vouched)` | ✅ SAT |
| 3 | `AG (P_S_Count <= 3)` | ✅ SAT |
| 4 | `AG (P_S_FraudFlagged = 1 AND P_W1_Vouched = 1 ⟹ EF P_W1_P...` | ❌ NOT SAT |
| 5 | `EF (P_S_Count = 3)` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_W1_Available + P_W1_Vouched = 1` holds in **all** reachable states
- `P_W2_Available + P_W2_Vouched = 1` holds in **all** reachable states
- `P_W3_Available + P_W3_Vouched = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_S_FraudFlagged | 14 |
| P_W1_Penalty | 11 |
| P_W2_Penalty | 11 |
| P_W3_Penalty | 11 |
| P_S_Count | 3 |
| P_W1_Available | 1 |
| P_W1_Vouched | 1 |
| P_W2_Available | 1 |
| P_W2_Vouched | 1 |
| P_W3_Available | 1 |
| P_W3_Vouched | 1 |
