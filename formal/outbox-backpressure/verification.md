## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 4 |
| **Reachable states** | **52** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Ingress + P_Outbox + P_DTN + P_Delivered = 5) EF (P...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_Outbox + P_OutboxSlotsFree = 3` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_DTN | 5 |
| P_Delivered | 5 |
| P_Ingress | 5 |
| P_Outbox | 3 |
| P_OutboxSlotsFree | 3 |
