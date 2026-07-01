## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 3 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Pool >= 1)` | ❌ NOT SAT |
| 2 | `AG (P_Pool = 1 ⟹ EF (P_Pool >= 3))` | ❌ NOT SAT |
| 3 | `AG (P_Pool <= 7)` | ❌ NOT SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Pool | 76 |
| P_RefillTrigger | 49 |
| P_RefillCounter1 | 0 |
| P_RefillCounter2 | 0 |
