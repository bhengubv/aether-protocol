## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 3 |
| **Reachable states** | **10001** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_GroupKey_v1 + P_GroupKey_v2 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_LeftMember_HasOldKey | 100 |
| P_MemberLeaveEvent | 99 |
| P_GroupKey_v1 | 1 |
| P_GroupKey_v2 | 1 |
