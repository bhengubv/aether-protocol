## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 7 |
| Transitions | 4 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG ((P_BLE_Up + P_WifiDirect_Up + P_LoRa_Up >= 1) ⟹ EF (P...` | ✅ SAT |
| 2 | `AG ¬ (P_BLE_Down = 1 AND P_Selected_BLE = 1 AND P_WifiDir...` | ✅ SAT |

### Conservation Invariants (auto-discovered)

- `P_BLE_Down + P_BLE_Up = 1` holds in **all** reachable states
- `P_LoRa_Up + P_WifiDirect_Up = 2` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Selected_BLE | 17 |
| P_Selected_LoRa | 16 |
| P_Selected_WifiDirect | 16 |
| P_BLE_Down | 1 |
| P_BLE_Up | 1 |
| P_LoRa_Up | 1 |
| P_WifiDirect_Up | 1 |
