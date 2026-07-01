## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 5 |
| Transitions | 3 |
| **Reachable states** | **10** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Played >= 3) AG (P_Played + P_Buffer + P_PacketInOr...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `sum(all) = 3` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Buffer | 3 |
| P_PacketInOrder | 3 |
| P_Played | 3 |
| P_Discarded | 0 |
| P_LatePacket | 0 |
