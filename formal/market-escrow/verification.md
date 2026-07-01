## Machine-Checked Verification (`tools/verify.py`)

| Metric | Value |
|---|---|
| Places | 8 |
| Transitions | 5 |
| **Reachable states** | **2000** |
| Goal reachable | ✅ YES |
| Safety violations | ✅ none |

### CTL Query Verification (`.q` file)

| # | Query | Result |
|---|---|---|
| 1 | `AG (P_Buyer_HasFunds + P_Escrow_Funds + P_Seller_HasFunds...` | ✅ SAT |
| 2 | `EF (P_Buyer_HasVault = 1 AND P_Seller_HasFunds = 100)` | ✅ SAT |
| 3 | `EF (P_DisputeResolved = 1 AND P_Buyer_HasFunds = 100 AND ...` | ✅ SAT |
| 4 | `AG ¬ (P_Buyer_HasVault = 1 AND P_Seller_HasFunds = 0)` | ✅ SAT |

### Boundedness (max token count per place)

| Place | Max tokens |
|---|---|
| P_Buyer_HasFunds | 100 |
| P_Escrow_Funds | 100 |
| P_Seller_HasFunds | 100 |
| P_DisputeRaised | 56 |
| P_DisputeResolved | 14 |
| P_Buyer_HasVault | 1 |
| P_Escrow_Vault | 1 |
| P_Seller_HasVault | 1 |
