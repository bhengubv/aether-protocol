# AetherNet.Cartography

Walk-to-earn trust layer for AetherNet — coordinate-bound **Proof-of-Location** plus a map-data-quality
(content-trust) score, with no server.

A location claim is only as good as the witnesses who co-sign it. Cartography's Proof-of-Location is
**short-range, witness-co-signed, and Sybil-resistant**: a walker's presence at a coarse cell is attested by
nearby peers, aggregated once enough independent witnesses concur, and weighed by earned reputation so a farm
of fresh bots counts for nothing. Raw phone GPS is never trusted on its own — only the witness-concurred cell.

It is the primitive a map-contribution reward is gated on: the proof that someone was really there, and that
the data they added is trustworthy. Pure managed code over `AetherNet.Core` + `AetherNet.Security`, portable
to the other seven language SDKs.
