# SPDX-License-Identifier: MIT

"""Tests for :class:`aethernet.storage.DerivedDataAtRestKeyProvider`.

Mirrors the C# ``DerivedDataAtRestKeyProviderTests``: PBKDF2-HMAC-SHA256
derivation, validation, caching, and the rotation flow.

All tests use a low iteration count (1000) to keep the suite fast — the
production default is 600,000, which would add ~100 ms per derivation.
The math is identical regardless of iteration count, so the test
coverage is equivalent.
"""

from __future__ import annotations

import os

import pytest

from aethernet.storage import (
    DerivedDataAtRestKeyProvider,
    EncryptedKeyValueStore,
    InMemoryKeyValueStore,
)


_TEST_ITERATIONS = 1000  # production default is 600_000


def test_derives_a_32_byte_aes256_key():
    salt = os.urandom(16)
    p = DerivedDataAtRestKeyProvider("correct horse battery staple", salt, _TEST_ITERATIONS)

    assert p.current_version == 1
    key = p.get_key(1)
    assert key is not None
    assert len(key) == 32


def test_same_passphrase_and_salt_yields_same_key():
    salt = os.urandom(16)
    a = DerivedDataAtRestKeyProvider("hello", salt, _TEST_ITERATIONS)
    b = DerivedDataAtRestKeyProvider("hello", salt, _TEST_ITERATIONS)
    assert a.get_key(1) == b.get_key(1)


def test_different_salt_yields_different_key():
    a = DerivedDataAtRestKeyProvider("hello", os.urandom(16), _TEST_ITERATIONS)
    b = DerivedDataAtRestKeyProvider("hello", os.urandom(16), _TEST_ITERATIONS)
    assert a.get_key(1) != b.get_key(1)


def test_different_passphrase_yields_different_key():
    salt = os.urandom(16)
    a = DerivedDataAtRestKeyProvider("hello", salt, _TEST_ITERATIONS)
    b = DerivedDataAtRestKeyProvider("world", salt, _TEST_ITERATIONS)
    assert a.get_key(1) != b.get_key(1)


def test_rejects_short_salt():
    with pytest.raises(ValueError, match="salt must be at least"):
        DerivedDataAtRestKeyProvider("hello", os.urandom(15), _TEST_ITERATIONS)


def test_rejects_empty_passphrase():
    with pytest.raises(ValueError, match="passphrase"):
        DerivedDataAtRestKeyProvider("", os.urandom(16), _TEST_ITERATIONS)


def test_rejects_zero_iterations():
    with pytest.raises(ValueError, match="iterations"):
        DerivedDataAtRestKeyProvider("hello", os.urandom(16), 0)


def test_with_rotation_keeps_old_key_readable():
    salt_v1 = os.urandom(16)
    salt_v2 = os.urandom(16)

    v1 = DerivedDataAtRestKeyProvider("v1-pass", salt_v1, _TEST_ITERATIONS)
    rotated = v1.with_rotation(2, "v2-pass", salt_v2, _TEST_ITERATIONS)

    assert rotated.current_version == 2
    # Both versions still derivable.
    assert rotated.get_key(1) == v1.get_key(1)
    assert rotated.get_key(2) is not None
    assert rotated.get_key(2) != rotated.get_key(1)


def test_with_rotation_rejects_existing_version():
    p = DerivedDataAtRestKeyProvider("hello", os.urandom(16), _TEST_ITERATIONS)
    with pytest.raises(ValueError, match="already exists"):
        p.with_rotation(1, "world", os.urandom(16), _TEST_ITERATIONS)


def test_with_rotation_rejects_out_of_range_version():
    p = DerivedDataAtRestKeyProvider("hello", os.urandom(16), _TEST_ITERATIONS)
    with pytest.raises(ValueError, match="single byte"):
        p.with_rotation(0, "world", os.urandom(16), _TEST_ITERATIONS)
    with pytest.raises(ValueError, match="single byte"):
        p.with_rotation(256, "world", os.urandom(16), _TEST_ITERATIONS)


@pytest.mark.asyncio
async def test_end_to_end_with_encrypted_kv():
    salt = os.urandom(16)
    provider = DerivedDataAtRestKeyProvider("the-passphrase", salt, _TEST_ITERATIONS)
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, provider)

    await store.put("k", b"derived-key-roundtrip")
    assert await store.get("k") == b"derived-key-roundtrip"

    # A different passphrase yields a different derived key — must NOT decrypt.
    other = DerivedDataAtRestKeyProvider("wrong-passphrase", salt, _TEST_ITERATIONS)
    other_store = EncryptedKeyValueStore(inner, other)
    assert await other_store.get("k") is None


def test_iterations_property_reflects_constructor_argument():
    p = DerivedDataAtRestKeyProvider("hello", os.urandom(16), 12345)
    assert p.iterations == 12345
