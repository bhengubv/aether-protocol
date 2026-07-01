## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 4 |
| Transitions | 4 |
| **Reachable states** | **2001** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_FullMesh + P_SFU = 1) EF (P_SFU = 1) EF (P_FrameQue...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_FullMesh + P_SFU = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Participants | 48 |
| P_FrameQueue | 45 |
| P_FullMesh | 1 |
| P_SFU | 1 |
