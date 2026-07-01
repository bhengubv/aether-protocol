## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 10 |
| Transitions | 5 |
| **Reachable states** | **5** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1 AND P_Host_...` | ✅ SAT |
| 2 | `EF (P_F1_AtHostPos = 1 AND P_F2_AtHostPos = 1)` | ✅ SAT |
| 3 | `AG (P_F1_AtHostPos = 1 ⟹ AG P_F1_AtHostPos = 1) AG (P_F2_...` | ✅ SAT |
| 4 | `AG (P_F1_AtHostPos = 1 ⟹ P_F1_SyncApplied = 1) AG (P_F2_A...` | ✅ SAT |
| 5 | `AG (P_Host_Playing = 1 ⟹ (P_SyncTo_F1 + P_F1_SyncApplied ...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_F1_AtHostPos + P_F1_AtStart = 1` holds in **all** reachable states
- `P_F1_AtStart + P_F1_SyncApplied = 1` holds in **all** reachable states
- `P_F2_AtHostPos + P_F2_AtStart = 1` holds in **all** reachable states
- `P_F2_AtStart + P_F2_SyncApplied = 1` holds in **all** reachable states
- `P_Host_Paused + P_Host_Playing = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_F1_AtHostPos | 1 |
| P_F1_AtStart | 1 |
| P_F1_SyncApplied | 1 |
| P_F2_AtHostPos | 1 |
| P_F2_AtStart | 1 |
| P_F2_SyncApplied | 1 |
| P_Host_Paused | 1 |
| P_Host_Playing | 1 |
| P_SyncTo_F1 | 1 |
| P_SyncTo_F2 | 1 |
