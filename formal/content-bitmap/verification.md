## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 2 |
| **Reachable states** | **15** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Receiver_Has = 4) AG (P_Sender_Has = 4)` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 8` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_InFlight | 4 |
| P_Receiver_Has | 4 |
| P_Receiver_Missing | 4 |
| P_Sender_Has | 4 |
