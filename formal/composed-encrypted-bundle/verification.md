## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 7 |
| Transitions | 5 |
| **Reachable states** | **6** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_DecryptedAtBob = 1) AG (P_Plaintext + P_Encrypted +...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 2` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_DecryptedAtBob | 1 |
| P_DeliveredBundle | 1 |
| P_Encrypted | 1 |
| P_InCustody | 1 |
| P_Plaintext | 1 |
| P_RouteAvailable | 1 |
| P_Routed | 1 |
