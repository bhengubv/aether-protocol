# Transport Selector — Properties

## P1 — Always Selects If Available
When any P_X_Up token exists, some T_SelectX is enabled. ✓

## P2 — No Stuck on Failed Transport
T_SelectBLE requires P_BLE_Up (test arc). When BLE fails, this transition is disabled. ✓
