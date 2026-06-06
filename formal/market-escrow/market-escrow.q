/* Market Escrow queries */

/* Conservation: funds always sum to 100, vault always 1 */
AG (P_Buyer_HasFunds + P_Escrow_Funds + P_Seller_HasFunds = 100)
AG (P_Seller_HasVault + P_Escrow_Vault + P_Buyer_HasVault = 1)

/* Atomic settle reachable */
EF (P_Buyer_HasVault = 1 AND P_Seller_HasFunds = 100)

/* Refund path reachable */
EF (P_DisputeResolved = 1 AND P_Buyer_HasFunds = 100 AND P_Seller_HasVault = 1)

/* No half-settle: buyer can't have vault without seller having funds */
AG ¬ (P_Buyer_HasVault = 1 AND P_Seller_HasFunds = 0)
