/* Transport selector queries */
AG ((P_BLE_Up + P_WifiDirect_Up + P_LoRa_Up >= 1)
    ⟹ EF (P_Selected_BLE + P_Selected_WifiDirect + P_Selected_LoRa >= 1))

AG ¬ (P_BLE_Down = 1 AND P_Selected_BLE = 1 AND P_WifiDirect_Up = 0 AND P_LoRa_Up = 0)
