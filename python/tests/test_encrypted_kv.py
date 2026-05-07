# SPDX-License-Identifier: MIT

"""Tests for :class:`aether.storage.EncryptedKeyValueStore`.

Covers the AES-256-GCM round trip, MAC failure on wrong-key reads, tamper
detection, key-version rotation, and composition with the existing KV
adapters. Mirrors the C# ``EncryptedKeyValueStoreTests`` test set.
"""

from __future__ import annotations

import os

import pytest

from aether.storage import (
    EncryptedKeyValueStore,
    InMemoryKeyValueStore,
    StaticDataAtRestKeyProvider,
)


def _random_key() -> bytes:
    return os.urandom(EncryptedKeyValueStore.KEY_SIZE)


@pytest.mark.asyncio
async def test_put_then_get_round_trips_original_bytes():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    value = b"aether-state-payload"
    await store.put("signal:session:peer-1", value)

    got = await store.get("signal:session:peer-1")
    assert got == value


@pytest.mark.asyncio
async def test_inner_store_contains_ciphertext_not_plaintext():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    plaintext = b"very-secret-route-table-entry"
    await store.put("route:peer-X", plaintext)

    ciphertext = await inner.get("route:peer-X")
    assert ciphertext is not None
    assert ciphertext != plaintext

    # Wire format: 1 (version) + 12 (nonce) + N (ciphertext) + 16 (tag).
    expected_length = (
        EncryptedKeyValueStore.VERSION_HEADER_SIZE
        + EncryptedKeyValueStore.NONCE_SIZE
        + len(plaintext)
        + EncryptedKeyValueStore.TAG_SIZE
    )
    assert len(ciphertext) == expected_length

    # Version header defaults to 1 for the single-key constructor.
    assert ciphertext[0] == 1

    # Plaintext substring must NOT appear in the blob.
    assert plaintext not in ciphertext


@pytest.mark.asyncio
async def test_wrong_key_cannot_decrypt_returns_none():
    inner = InMemoryKeyValueStore()
    key_a = _random_key()
    key_b = _random_key()

    writer = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(key_a))
    await writer.put("k", b"payload")

    # Read under a DIFFERENT key (same version number, different bytes).
    reader = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(key_b))
    got = await reader.get("k")
    assert got is None


@pytest.mark.asyncio
async def test_unknown_key_version_returns_none():
    inner = InMemoryKeyValueStore()
    writer = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))
    await writer.put("k", b"payload")

    # Build a provider whose only key is on a different version number.
    provider_v2 = StaticDataAtRestKeyProvider(
        keys_by_version={2: _random_key()},
        current_version=2,
    )
    reader = EncryptedKeyValueStore(inner, provider_v2)
    assert await reader.get("k") is None


@pytest.mark.asyncio
async def test_tampered_ciphertext_fails_authentication_returns_none():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))
    await store.put("k", b"important-payload")

    # Flip a byte in the ciphertext middle (definitely inside the AES
    # output, not the version byte).
    blob = await inner.get("k")
    assert blob is not None
    tamper_index = (
        EncryptedKeyValueStore.VERSION_HEADER_SIZE
        + EncryptedKeyValueStore.NONCE_SIZE
        + 2
    )
    tampered = bytearray(blob)
    tampered[tamper_index] ^= 0x01
    await inner.put("k", bytes(tampered))

    assert await store.get("k") is None


@pytest.mark.asyncio
async def test_tampered_tag_fails_authentication_returns_none():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))
    await store.put("k", b"important-payload")

    blob = await inner.get("k")
    assert blob is not None
    # Flip a byte in the trailing 16-byte tag.
    tampered = bytearray(blob)
    tampered[-1] ^= 0x01
    await inner.put("k", bytes(tampered))

    assert await store.get("k") is None


@pytest.mark.asyncio
async def test_truncated_blob_below_minimum_returns_none():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    # Write a payload that's shorter than the minimum well-formed blob.
    await inner.put("garbage", b"\x00\x00\x00\x00\x00")

    assert await store.get("garbage") is None


@pytest.mark.asyncio
async def test_key_rotation_old_version_remains_readable():
    inner = InMemoryKeyValueStore()
    key_v1 = _random_key()
    key_v2 = _random_key()

    # Phase 1: write under version 1.
    v1_provider = StaticDataAtRestKeyProvider(
        keys_by_version={1: key_v1}, current_version=1)
    v1_store = EncryptedKeyValueStore(inner, v1_provider)
    await v1_store.put("legacy", b"written-under-v1")

    legacy_blob = await inner.get("legacy")
    assert legacy_blob is not None
    assert legacy_blob[0] == 1

    # Phase 2: rotate. New provider holds BOTH versions, current=2.
    rotating = StaticDataAtRestKeyProvider(
        keys_by_version={1: key_v1, 2: key_v2},
        current_version=2,
    )
    rotating_store = EncryptedKeyValueStore(inner, rotating)

    # Old value still decryptable via v1 key.
    assert await rotating_store.get("legacy") == b"written-under-v1"

    # New writes use v2.
    await rotating_store.put("fresh", b"written-under-v2")
    fresh_blob = await inner.get("fresh")
    assert fresh_blob is not None
    assert fresh_blob[0] == 2

    # After rewrap, every blob is on v2.
    rewrapped = await rotating_store.rewrap()
    assert rewrapped == 2

    legacy_rewrapped = await inner.get("legacy")
    assert legacy_rewrapped is not None
    assert legacy_rewrapped[0] == 2

    # Phase 3: a v2-only provider can still read everything.
    v2_only = StaticDataAtRestKeyProvider(
        keys_by_version={2: key_v2}, current_version=2)
    v2_store = EncryptedKeyValueStore(inner, v2_only)
    assert await v2_store.get("legacy") == b"written-under-v1"
    assert await v2_store.get("fresh") == b"written-under-v2"


@pytest.mark.asyncio
async def test_nonces_are_unique_across_writes_of_same_value():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    seen: set[bytes] = set()
    for i in range(32):
        await store.put(f"k-{i}", b"identical-plaintext-every-time")
        blob = await inner.get(f"k-{i}")
        assert blob is not None
        nonce = blob[
            EncryptedKeyValueStore.VERSION_HEADER_SIZE:
            EncryptedKeyValueStore.VERSION_HEADER_SIZE + EncryptedKeyValueStore.NONCE_SIZE
        ]
        assert nonce not in seen, "AES-GCM nonce reuse — fatal for confidentiality."
        seen.add(nonce)


@pytest.mark.asyncio
async def test_remove_contains_list_pass_through_inner():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    assert not await store.contains("k")
    await store.put("k", b"v")
    assert await store.contains("k")

    listed = []
    async for k in store.list_keys():
        listed.append(k)
    assert listed == ["k"]

    assert await store.remove("k")
    assert not await store.contains("k")
    assert not await store.remove("k")


@pytest.mark.asyncio
async def test_empty_value_round_trips():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    await store.put("k", b"")
    got = await store.get("k")
    assert got == b""


@pytest.mark.asyncio
async def test_large_payload_round_trips():
    inner = InMemoryKeyValueStore()
    store = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    big = os.urandom(1024 * 1024)  # 1 MiB
    await store.put("k", big)
    assert await store.get("k") == big


@pytest.mark.asyncio
async def test_constructor_rejects_none_arguments():
    with pytest.raises(ValueError):
        EncryptedKeyValueStore(None, StaticDataAtRestKeyProvider(_random_key()))
    with pytest.raises(ValueError):
        EncryptedKeyValueStore(InMemoryKeyValueStore(), None)


@pytest.mark.asyncio
async def test_wraps_signal_session_persistence():
    """End-to-end: wrap the inner KV with encryption, mount the
    SignalSessionStore on top, write/read a session via the
    SignalProtocolService, and verify the inner store holds ciphertext.
    """
    from aether.security import SignalProtocolService
    from aether.security.session_store import KeyValueSignalSessionStore
    from aether.security.pre_key_store import KeyValuePreKeyStore

    inner = InMemoryKeyValueStore()
    encrypted = EncryptedKeyValueStore(inner, StaticDataAtRestKeyProvider(_random_key()))

    bob_inner = InMemoryKeyValueStore()
    bob_encrypted = EncryptedKeyValueStore(bob_inner, StaticDataAtRestKeyProvider(_random_key()))

    alice = SignalProtocolService(
        session_store=KeyValueSignalSessionStore(encrypted),
        pre_key_store=KeyValuePreKeyStore(encrypted),
    )
    bob = SignalProtocolService(
        session_store=KeyValueSignalSessionStore(bob_encrypted),
        pre_key_store=KeyValuePreKeyStore(bob_encrypted),
    )

    await alice.generate_pre_key_bundle("alice-encrypted-uhid")
    bob_bundle = await bob.generate_pre_key_bundle("bob-encrypted-uhid")
    await alice.process_pre_key_bundle(bob_bundle)

    msg = await alice.encrypt("bob-encrypted-uhid", b"hello-encrypted-at-rest")
    plain = await bob.decrypt("alice-encrypted-uhid", msg)
    assert plain == b"hello-encrypted-at-rest"

    # The inner store must NOT contain the plaintext UHIDs anywhere
    # (those would otherwise leak via the JSON keys of skipped_message_keys
    # or session bytes).
    async for inner_key in inner.list_keys():
        blob = await inner.get(inner_key)
        assert blob is not None
        assert b"alice-encrypted-uhid" not in blob
        assert b"bob-encrypted-uhid" not in blob


@pytest.mark.asyncio
async def test_static_key_provider_rejects_invalid_key_size():
    with pytest.raises(ValueError):
        StaticDataAtRestKeyProvider(b"\x00" * 16)  # too short
    with pytest.raises(ValueError):
        StaticDataAtRestKeyProvider(b"\x00" * 64)  # too long
