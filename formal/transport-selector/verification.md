## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 7 |
| Transitions | 4 |
| **Reachable states** | **10000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### Conservation Invariants (auto-discovered)

- `P_BLE_Down + P_BLE_Up = 1` holds in **all** reachable states
- `P_LoRa_Up + P_WifiDirect_Up = 2` holds in **all** reachable states

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Selected_BLE | 30 |
| P_Selected_LoRa | 29 |
| P_Selected_WifiDirect | 29 |
| P_BLE_Down | 1 |
| P_BLE_Up | 1 |
| P_LoRa_Up | 1 |
| P_WifiDirect_Up | 1 |
