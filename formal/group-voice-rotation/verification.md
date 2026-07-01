## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 3 |
| **Reachable states** | **2001** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_GroupKey_v2 = 1 AND P_GroupKey_v1 = 0) AG (P_LeftMe...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_GroupKey_v1 + P_GroupKey_v2 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_LeftMember_HasOldKey | 45 |
| P_MemberLeaveEvent | 44 |
| P_GroupKey_v1 | 1 |
| P_GroupKey_v2 | 1 |
