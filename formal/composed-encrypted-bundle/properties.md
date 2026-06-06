# Composed Encrypted Bundle — Properties

## P1 — End-to-End Delivery
Message starts at P_Plaintext, ends at P_DecryptedAtBob through the
full pipeline. ✓

## P2 — Conservation Across Subsystems
Exactly one message token in flight across all subsystem places. ✓

## P3 — Emergent Properties Composed
- Encryption (Signal): T_Encrypt is the only producer of P_Encrypted
- Routing (AODV): T_LookupRoute uses P_RouteAvailable test arc
- Custody (DTN): T_AcceptCustody + T_Deliver preserves identity
- Decryption: T_Decrypt is the only producer of P_DecryptedAtBob

By construction, each subsystem's property holds individually within
its transition slice. The composition proves they hold simultaneously.
