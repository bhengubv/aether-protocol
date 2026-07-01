## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 4 |
| **Reachable states** | **2000** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Rung_Low + P_Rung_Mid + P_Rung_High = 1) EF (P_Rung...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Segments_Delivered | 667 |
| P_BufferOk | 3 |
| P_Rung_High | 1 |
| P_Rung_Low | 1 |
| P_Rung_Mid | 1 |
