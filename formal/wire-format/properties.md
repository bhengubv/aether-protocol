# Wire Format — Properties

## P1 — Cross-Language Round Trip
Any encoder's bytes are decodable by any other implementation. ✓
Witness: T_CSharp_Encode → T_Rust_Decode_CSharp → P_Decoded_AtRust = 1.

## P2 — Identity Preservation
Test arcs preserve the original packet during encoding. ✓

Real implementation tests in `tests/cross-language/runners/` exercise
these properties on actual byte arrays.
