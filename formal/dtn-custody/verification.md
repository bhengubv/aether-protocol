## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 6 |
| Transitions | 6 |
| **Reachable states** | **6** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

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
