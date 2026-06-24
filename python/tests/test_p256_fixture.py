# SPDX-License-Identifier: MIT
"""Cross-language P-256 ECDSA verify fixture runner (Python).

Drives ``Ed25519SigningService.verify_with_fallback`` through the shared corpus at
``tests/cross-language/p256-fixtures.json`` — DER SubjectPublicKeyInfo public key +
ASN.1 DER ECDSA signature + SHA-256, per PROTOCOL_SPEC.md section 7.5. Every AetherNet
SDK drives the SAME vectors and MUST accept ``valid:true`` and reject ``valid:false``.
"""
import json
from pathlib import Path

from aethernet.security.ed25519_service import Ed25519SigningService


def _load_fixture():
    here = Path(__file__).resolve()
    for parent in here.parents:
        candidate = parent / "tests" / "cross-language" / "p256-fixtures.json"
        if candidate.is_file():
            return json.loads(candidate.read_text(encoding="utf-8"))
    raise FileNotFoundError(f"p256-fixtures.json not found walking up from {here}")


def test_verify_with_fallback_drives_every_p256_vector():
    doc = _load_fixture()
    vectors = doc["vectors"]
    assert vectors

    for v in vectors:
        pub = bytes.fromhex(v["public_key_der"])
        msg = bytes.fromhex(v["message"])
        sig = bytes.fromhex(v["signature_der"])
        # A >32-byte key forces the P-256 branch; a regression to "return verify()"
        # (Ed25519-only) would reject the valid vector and fail here.
        assert len(pub) > 32, v["name"]
        assert (
            Ed25519SigningService.verify_with_fallback(pub, msg, sig) == v["valid"]
        ), v["name"]
