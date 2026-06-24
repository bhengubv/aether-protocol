#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Generate the canonical P-256 ECDSA verification fixture for cross-language parity.

Canonical format (PROTOCOL_SPEC.md 7.5 — "DER-encoded P-256 key"):
  public key : X.509 SubjectPublicKeyInfo, DER  (hex)
  signature  : ASN.1 DER ECDSA SEQUENCE{r,s}    (hex)
  hash       : SHA-256
  message    : raw bytes                         (hex)

Every AetherNet SDK's verify-with-fallback must ACCEPT every {"valid": true}
vector and REJECT every {"valid": false} vector. The committed p256-fixtures.json
is the source of truth; this script documents how it was produced.
"""
import json
import os

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "p256-fixtures.json")

# Fixed test private scalar -> deterministic P-256 public key. NEVER a real key.
PRIV_SCALAR = 0x00C0FFEE_C0FFEE00_DEADBEEF_DEADBEEF_0BADCAFE_0BADCAFE_12345678_9ABCDEF0


def main() -> int:
    priv = ec.derive_private_key(PRIV_SCALAR, ec.SECP256R1())
    spki_der = priv.public_key().public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )

    msg = b"aethernet:p256-migration-verify:v1"
    sig_der = priv.sign(msg, ec.ECDSA(hashes.SHA256()))

    bad_sig = bytearray(sig_der)
    bad_sig[-1] ^= 0x01  # flip a byte -> DER-shaped but invalid math

    other_msg = b"aethernet:p256-migration-verify:TAMPERED"

    vectors = [
        {"name": "valid_p256_ecdsa_sha256",
         "public_key_der": spki_der.hex(), "message": msg.hex(),
         "signature_der": sig_der.hex(), "valid": True},
        {"name": "tampered_signature",
         "public_key_der": spki_der.hex(), "message": msg.hex(),
         "signature_der": bytes(bad_sig).hex(), "valid": False},
        {"name": "wrong_message",
         "public_key_der": spki_der.hex(), "message": other_msg.hex(),
         "signature_der": sig_der.hex(), "valid": False},
    ]

    doc = {
        "_comment": ("Canonical P-256 ECDSA verify fixture (PROTOCOL_SPEC.md 7.5): "
                     "DER SPKI public key + DER ECDSA signature + SHA-256. Every SDK "
                     "must accept valid:true and reject valid:false."),
        "curve": "secp256r1",
        "hash": "SHA-256",
        "public_key_encoding": "der-spki",
        "signature_encoding": "der",
        "vectors": vectors,
    }

    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)
        fh.write("\n")
    print(f"wrote {OUT} ({len(vectors)} vectors)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
