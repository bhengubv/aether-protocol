## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 3 |
| **Reachable states** | **5** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `sum(all) = 100` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_CreatorBalance | 100 |
| P_Pool | 100 |
| P_C1_Balance | 50 |
| P_C2_Balance | 50 |
