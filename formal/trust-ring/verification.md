## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 6 |
| Transitions | 5 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Attested = 1) AG (P_Attested = 1 ⟹ P_Signatures >= ...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Attested | 43 |
| P_Revoked | 21 |
| P_Signatures | 3 |
| P_V1_Available | 1 |
| P_V2_Available | 1 |
| P_V3_Available | 1 |
