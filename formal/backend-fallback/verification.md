## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 6 |
| **Reachable states** | **35** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Delivered >= 3) AG (P_Pending + P_TryMesh + P_TryDt...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 3` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Delivered | 3 |
| P_Pending | 3 |
| P_TryBackend | 3 |
| P_TryDtn | 3 |
| P_TryMesh | 3 |
