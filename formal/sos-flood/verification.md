## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 7 |
| Transitions | 3 |
| **Reachable states** | **4** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_N1_Alerted = 1 AND P_N2_Alerted = 1 AND P_N3_Alerte...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_N1_Alerted + P_SosToN1 = 1` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_TTL | 3 |
| P_N1_Alerted | 1 |
| P_N2_Alerted | 1 |
| P_N3_Alerted | 1 |
| P_SosToN1 | 1 |
| P_SosToN2 | 1 |
| P_SosToN3 | 1 |
