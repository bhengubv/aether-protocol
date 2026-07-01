## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 8 |
| Transitions | 10 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Recovered = 1)` | ✅ SAT |
| 2 | `AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 >= 2) ...` | ❌ NOT SAT |
| 3 | `AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 < 2) =...` | ❌ NOT SAT |
| 4 | `AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 = 2) =...` | ❌ NOT SAT |
| 5 | `AG (P_Has_Shard_1 + P_No_Shard_1 = 1 AND P_Has_Shard_2 + ...` | ✅ SAT |
| 6 | `EF (P_Lost = 1)` | ✅ SAT |
| 7 | `AG ((P_Has_Shard_1 + P_Has_Shard_2 + P_Has_Shard_3 >= 2) ...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_Has_Shard_1 + P_No_Shard_1 = 1` holds in **all** reachable states
- `P_Has_Shard_2 + P_No_Shard_2 = 1` holds in **all** reachable states
- `P_Has_Shard_3 + P_No_Shard_3 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Recovered | 58 |
| P_Lost | 55 |
| P_Has_Shard_1 | 1 |
| P_Has_Shard_2 | 1 |
| P_Has_Shard_3 | 1 |
| P_No_Shard_1 | 1 |
| P_No_Shard_2 | 1 |
| P_No_Shard_3 | 1 |
