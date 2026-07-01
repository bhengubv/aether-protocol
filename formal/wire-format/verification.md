## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 7 |
| Transitions | 6 |
| **Reachable states** | **2000** |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `EF (P_Decoded_AtCSharp = 1 AND P_Decoded_AtTypeScript = 1...` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Bytes_FromCSharp | 11 |
| P_Bytes_FromRust | 10 |
| P_Bytes_FromTypeScript | 10 |
| P_Decoded_AtCSharp | 5 |
| P_Decoded_AtRust | 5 |
| P_Decoded_AtTypeScript | 5 |
| P_Packet_Logical | 1 |
