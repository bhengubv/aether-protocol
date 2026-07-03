# SPDX-License-Identifier: MIT

"""Cross-language BIP-39 parity: the Python port must reproduce the official Trezor
test vectors byte-for-byte, matching the C# reference (src/AetherNet.Security/Backup)
and every other AetherNet SDK.

Loads fixtures/bip39/vectors.json (24 official English vectors, passphrase "TREZOR")
and asserts entropy->mnemonic->seed for all of them, then exercises the AetherNet
identity backup layer (32-byte Ed25519 seed <-> 24-word phrase) including the
checksum-enforced reject paths."""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

from aethernet.security.bip39 import (
    entropy_to_mnemonic,
    mnemonic_to_entropy,
    mnemonic_to_seed,
    is_valid,
    to_recovery_phrase,
    from_recovery_phrase,
    WORDLIST,
)
from aethernet.security.ed25519_service import Ed25519SigningService

# fixtures/bip39 lives at the repo root: tests/ -> python/ -> repo root.
_BIP39_DIR = Path(__file__).resolve().parents[2] / "fixtures" / "bip39"
_VECTORS = json.loads((_BIP39_DIR / "vectors.json").read_text(encoding="utf-8"))


def test_wordlist_is_the_official_2048_word_list() -> None:
    import hashlib

    assert len(WORDLIST) == 2048
    assert WORDLIST[0] == "abandon"
    assert WORDLIST[-1] == "zoo"
    # The canonical wordlist file is the words newline-joined with a trailing newline.
    joined = ("\n".join(WORDLIST) + "\n").encode("utf-8")
    assert (
        hashlib.sha256(joined).hexdigest()
        == "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda"
    )


def test_trezor_vectors_entropy_to_mnemonic_to_seed_byte_for_byte() -> None:
    vectors = _VECTORS["vectors"]
    passphrase = _VECTORS["passphrase"]
    assert passphrase == "TREZOR"
    assert len(vectors) == 24, f"expected 24 official vectors, got {len(vectors)}"

    for i, v in enumerate(vectors):
        entropy = bytes.fromhex(v["entropy"])
        mnemonic = v["mnemonic"]
        seed = v["seed"]

        # entropy -> mnemonic
        assert entropy_to_mnemonic(entropy) == mnemonic, f"vector {i} entropy->mnemonic"
        # mnemonic -> entropy (round-trip, checksum enforced)
        assert mnemonic_to_entropy(mnemonic).hex() == v["entropy"], (
            f"vector {i} mnemonic->entropy"
        )
        # mnemonic -> seed (PBKDF2-HMAC-SHA512, 2048 rounds, passphrase "TREZOR")
        assert mnemonic_to_seed(mnemonic, passphrase).hex() == seed, (
            f"vector {i} mnemonic->seed"
        )
        # every official vector is a well-formed phrase
        assert is_valid(mnemonic), f"vector {i} is_valid"


def test_identity_recovery_phrase_known_vector() -> None:
    entropy = bytes.fromhex(
        "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f"
    )
    expected_phrase = (
        "void come effort suffer camp survey warrior heavy shoot primary clutch crush "
        "open amazing screen patrol group space point ten exist slush involve unfold"
    )

    phrase = to_recovery_phrase(entropy)
    assert phrase == expected_phrase
    assert len(phrase.split()) == 24

    public_key, private_key = from_recovery_phrase(phrase)
    assert private_key == entropy
    assert len(public_key) == 32
    # The public key must be the Ed25519 point derived from this exact seed, which is
    # also what the SDK's own signing service produces for the same private key.
    _, expected_public = _keypair_from_seed(entropy)
    assert public_key == expected_public


def _keypair_from_seed(seed: bytes):
    """Independent Ed25519 public-key derivation from a seed (PyNaCl path used by the
    SDK's signing service) to cross-check the cryptography-package derivation in bip39."""
    import nacl.signing

    signing_key = nacl.signing.SigningKey(seed)
    return bytes(signing_key), bytes(signing_key.verify_key)


def test_random_seed_roundtrip_restores_signing_identity() -> None:
    # A fresh Ed25519 identity: 32-byte seed private key + 32-byte public key.
    private_key, public_key = Ed25519SigningService.generate_keypair()
    assert len(private_key) == 32 and len(public_key) == 32

    phrase = to_recovery_phrase(private_key)
    assert len(phrase.split()) == 24
    assert is_valid(phrase)

    restored_public, restored_private = from_recovery_phrase(phrase)
    assert restored_private == private_key
    # The restored public key matches the original identity exactly.
    assert restored_public == public_key

    # The restored key really is the identity: it signs, and the restored public
    # key verifies the signature.
    message = b"aethernet identity restored from recovery phrase"
    signature = Ed25519SigningService.sign(restored_private, message)
    assert Ed25519SigningService.verify(restored_public, message, signature)
    # and cross-checks against the original public key too
    assert Ed25519SigningService.verify(public_key, message, signature)


def test_reject_bad_checksum_all_abandon() -> None:
    # 24x "abandon" is a valid word sequence but a deliberately invalid checksum.
    bad = " ".join(["abandon"] * 24)
    assert not is_valid(bad)
    with pytest.raises(ValueError):
        mnemonic_to_entropy(bad)
    with pytest.raises(ValueError):
        from_recovery_phrase(bad)


def test_reject_unknown_word() -> None:
    # Take a known-good 24-word phrase and corrupt one word to a non-wordlist token.
    good = to_recovery_phrase(
        bytes.fromhex(
            "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f"
        )
    )
    words = good.split()
    words[0] = "notabip39word"
    bad = " ".join(words)
    assert not is_valid(bad)
    with pytest.raises(ValueError):
        mnemonic_to_entropy(bad)


def test_reject_wrong_word_count() -> None:
    three = "abandon ability able"
    assert not is_valid(three)
    with pytest.raises(ValueError):
        mnemonic_to_entropy(three)
    with pytest.raises(ValueError):
        from_recovery_phrase(three)


def test_to_recovery_phrase_requires_32_byte_seed() -> None:
    with pytest.raises(ValueError):
        to_recovery_phrase(bytes(16))
    with pytest.raises(ValueError):
        to_recovery_phrase(bytes(31))


def test_from_recovery_phrase_requires_24_words() -> None:
    # A valid 12-word phrase decodes to 16 bytes, which is not a 256-bit identity seed.
    twelve = _VECTORS["vectors"][0]["mnemonic"]  # 12 words, valid checksum
    assert len(twelve.split()) == 12
    assert is_valid(twelve)  # valid BIP-39...
    with pytest.raises(ValueError):  # ...but not a valid AetherNet identity phrase
        from_recovery_phrase(twelve)
