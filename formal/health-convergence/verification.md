## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 6 |
| Transitions | 3 |
| **Reachable states** | **5** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `sum(all) = 3` holds in **all** reachable states
- `P_DTN_Degraded + P_DTN_Healthy = 1` holds in **all** reachable states
- `P_OverallNotYetReady + P_Overall_Healthy = 1` holds in **all** reachable states
- `P_Routing_Degraded + P_Routing_Healthy = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_DTN_Degraded | 1 |
| P_DTN_Healthy | 1 |
| P_OverallNotYetReady | 1 |
| P_Overall_Healthy | 1 |
| P_Routing_Degraded | 1 |
| P_Routing_Healthy | 1 |
