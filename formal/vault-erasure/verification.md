## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 8 |
| Transitions | 10 |
| **Reachable states** | **10000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_Has_Shard_1 + P_No_Shard_1 = 1` holds in **all** reachable states
- `P_Has_Shard_2 + P_No_Shard_2 = 1` holds in **all** reachable states
- `P_Has_Shard_3 + P_No_Shard_3 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Recovered | 136 |
| P_Lost | 134 |
| P_Has_Shard_1 | 1 |
| P_Has_Shard_2 | 1 |
| P_Has_Shard_3 | 1 |
| P_No_Shard_1 | 1 |
| P_No_Shard_2 | 1 |
| P_No_Shard_3 | 1 |
