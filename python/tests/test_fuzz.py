# SPDX-License-Identifier: MIT

"""Fuzz tests for the Python deserializers.

Mirrors the Go fuzz harness (``go/protocol/fuzz_serializer_test.go``) and
the C# ``PacketSerializerFuzzTests``: the deserializer parses untrusted
bytes off the wire, so the contract is — for ANY input it must EITHER
return a valid object OR raise a documented exception. The documented
exception set is:

* :class:`ValueError` / :class:`struct.error` / :class:`UnicodeDecodeError`
  — wire-format / utf-8 decoding failures
* :class:`OSError` — :func:`datetime.fromtimestamp` rejects out-of-range
  ``timestamp_ms`` on Windows (Errno 22) for years past ~3000; this is
  platform-specific Python runtime behaviour, not a serializer bug
* :class:`AttributeError` / :class:`TypeError` — JSON-decoded value is
  the wrong shape (e.g. a number where a dict is expected); fuzz inputs
  hit this on session-store paths
* :class:`json.JSONDecodeError` / :class:`binascii.Error` — base64
  / JSON failures on session-store paths

It must NEVER:

* raise an undocumented exception (anything else escaping = bug),
* hang in an infinite loop,
* allocate gigabytes from an attacker-controlled length prefix.

Two flavours run here:

1. Property tests over ``serialize -> deserialize`` round-trip with
   :mod:`hypothesis`-generated :class:`MeshPacket` inputs (random uhids,
   payloads up to 64KB, all packet types).

2. Direct fuzzers over ``deserialize(bytes)`` and the
   :class:`StoredSignalSession` JSON codec with the
   :func:`hypothesis.strategies.binary` strategy — assert no
   undocumented exception escapes.

Hypothesis settings: 1000 examples per test, ``deadline=None`` so a slow
example doesn't false-negative the suite. Run from ``python/``:

    python -m pytest tests/test_fuzz.py --hypothesis-show-statistics -q

Local adversarial runs can crank ``--hypothesis-seed`` and
``HYPOTHESIS_PROFILE=ci`` to push deeper.
"""

from __future__ import annotations

import json
import struct
from typing import Any
from uuid import UUID, uuid4

import pytest
from hypothesis import HealthCheck, given, settings, strategies as st

from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer
from aether.security.dtos import StoredSignalSession
from aether.security.session_store import deserialize_session, serialize_session


# ─── Settings profiles ─────────────────────────────────────────────────

# Each fuzz case runs 1000 hypothesis examples by default. The deadline
# is disabled because random inputs can land in slow paths on a busy CI
# host, and we do not want flakes — a real perf regression is caught by
# the bench harness, not here.
_FUZZ = settings(
    max_examples=1000,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow, HealthCheck.data_too_large],
)


# ─── Strategies ────────────────────────────────────────────────────────

# Bound payloads to 64KB so each iteration stays under a few ms; the
# actual wire format is u32-length-prefixed so even huge payloads round
# trip — the bench harness covers the perf side.
_payload = st.binary(max_size=65536)
_uhid = st.text(
    alphabet=st.characters(blacklist_categories=("Cs",)),  # no surrogates
    max_size=255,
)
_nonce = st.binary(min_size=0, max_size=255)
_signature = st.binary(min_size=0, max_size=255)
_packet_type = st.sampled_from(list(PacketType))


# Timestamp_ms is bounded to within Python's representable
# :func:`datetime.fromtimestamp` range on all platforms
# (Windows tops out around year 3000 with OSError). The wire format
# itself accepts the full int64 range — the platform-specific overflow
# is checked separately in the random-bytes fuzz, which accepts OSError
# as a documented exception type.
_MIN_TIMESTAMP_MS = 0
_MAX_TIMESTAMP_MS = 32_503_680_000_000  # ~year 3000 in unix-ms


@st.composite
def _mesh_packets(draw: Any) -> MeshPacket:
    """Hypothesis strategy producing realistically-shaped MeshPackets."""
    return MeshPacket(
        id=UUID(int=draw(st.integers(min_value=0, max_value=2**128 - 1))),
        type=draw(_packet_type),
        source_uhid=draw(_uhid),
        destination_uhid=draw(_uhid),
        ttl=draw(st.integers(min_value=0, max_value=255)),
        priority=draw(st.integers(min_value=0, max_value=255)),
        payload=draw(_payload),
        packet_nonce=draw(_nonce),
        signature=draw(_signature),
        timestamp_ms=draw(st.integers(
            min_value=_MIN_TIMESTAMP_MS, max_value=_MAX_TIMESTAMP_MS
        )),
        protocol_version=draw(st.integers(min_value=0, max_value=255)),
    )


# ─── PacketSerializer round-trip ───────────────────────────────────────

@_FUZZ
@given(packet=_mesh_packets())
def test_packet_serialize_deserialize_round_trip(packet: MeshPacket) -> None:
    """For ANY hypothesis-shaped MeshPacket, serialize -> deserialize
    must reproduce the wire-significant fields exactly.

    The non-wire fields (``created_at`` is reconstructed from
    ``timestamp_ms``) are checked separately.
    """
    wire = PacketSerializer.serialize(packet)
    got = PacketSerializer.deserialize(wire)

    assert got.id == packet.id
    assert got.type == packet.type
    assert got.source_uhid == packet.source_uhid
    assert got.destination_uhid == packet.destination_uhid
    assert got.ttl == packet.ttl
    assert got.priority == packet.priority
    assert got.payload == packet.payload
    assert got.packet_nonce == packet.packet_nonce
    assert got.signature == packet.signature
    assert got.timestamp_ms == packet.timestamp_ms
    assert got.protocol_version == packet.protocol_version


# ─── PacketSerializer.deserialize fuzz ─────────────────────────────────

# Hand-picked truncated buffers — must raise a documented exception,
# never crash with an unhandled error.
_HAND_PICKED_TOO_SHORT = [
    b"",
    b"\x00",
    b"\x01\x02",
    b"\x01\x02\x03\x04",
    b"\x01\x02\x03\x04\x05",
]


@pytest.mark.parametrize("data", _HAND_PICKED_TOO_SHORT)
def test_packet_deserialize_too_short_raises(data: bytes) -> None:
    """Pin the minimum-length contract: < 31 bytes -> ValueError."""
    with pytest.raises((ValueError, struct.error)):
        PacketSerializer.deserialize(data)


def _build_header_with_large_payload_length(payload_len: int) -> bytes:
    """43-byte header with valid scalar fields, zero-length u16 prefixes,
    and the supplied payload-length prefix — typically huge so we assert
    that the deserializer detects truncation rather than blindly allocating.
    """
    buf = bytearray(43)
    buf[0] = 0x02  # version
    buf[1] = 0x03  # PacketType.Data
    # bytes 2..17 left as zeros (uuid)
    buf[18] = 0x05  # priority
    struct.pack_into("<i", buf, 19, 7)  # ttl
    struct.pack_into("<q", buf, 23, 1234567890000)  # ts
    # 3 zero-length prefixes occupy 31..36 (already zero)
    struct.pack_into("<i", buf, 37, payload_len)
    # sigLen at 41..42 left zero
    return bytes(buf)


@pytest.mark.parametrize("oversize", [0x7FFFFFFF, 0x10000000, 0x01000000])
def test_packet_deserialize_oversize_payload_raises(oversize: int) -> None:
    """Mirrors the Go ``OversizePayloadLength`` test — payload-length
    claims hundreds of MB but the buffer is short. Must reject, not
    allocate.
    """
    buf = _build_header_with_large_payload_length(oversize)
    with pytest.raises((ValueError, struct.error)):
        PacketSerializer.deserialize(buf)


def test_packet_deserialize_negative_payload_length_raises() -> None:
    """Pin the ``len < 0 is rejected`` contract."""
    buf = _build_header_with_large_payload_length(-1)
    with pytest.raises((ValueError, struct.error)):
        PacketSerializer.deserialize(buf)


def test_packet_deserialize_oversize_uhid_prefix_raises() -> None:
    """31-byte fixed header + 0xFFFF UHID-length prefix with no following
    bytes — must fail clean, not allocate 64KB.
    """
    buf = bytearray(33)
    buf[31] = 0xFF
    buf[32] = 0xFF
    with pytest.raises((ValueError, struct.error, UnicodeDecodeError)):
        PacketSerializer.deserialize(bytes(buf))


@_FUZZ
@given(data=st.binary(min_size=0, max_size=8192))
def test_packet_deserialize_random_bytes_never_crashes(data: bytes) -> None:
    """1000 hypothesis-generated random buffers through deserialize.

    The contract is: EITHER returns a valid MeshPacket, OR raises one of
    the documented exception types. ANY other escaping exception is a
    bug — Python's call stack should not be reached by attacker-shaped
    bytes.
    """
    try:
        pkt = PacketSerializer.deserialize(data)
    except (ValueError, struct.error, UnicodeDecodeError, OSError):
        # Documented failure modes — fine. OSError covers the Windows
        # datetime.fromtimestamp overflow on out-of-range timestamps.
        return
    # Success path — must not silently return None.
    assert pkt is not None


@_FUZZ
@given(packet=_mesh_packets(), mutation_count=st.integers(min_value=1, max_value=4))
def test_packet_deserialize_mutated_valid_wire_never_crashes(
    packet: MeshPacket, mutation_count: int
) -> None:
    """Build a valid wire envelope, mutate 1..4 random byte positions,
    and verify the deserializer never raises an undocumented exception.

    Catches edge cases the wholly-random sweep skips (mostly-correct
    headers, length-prefix off-by-ones, etc.).
    """
    valid = bytearray(PacketSerializer.serialize(packet))
    if not valid:
        return
    # Use the packet's first uuid byte as a deterministic mutation seed.
    seed = packet.id.bytes[0]
    for i in range(mutation_count):
        pos = (seed * 31 + i * 7) % len(valid)
        valid[pos] = (valid[pos] + 0x5A + i) & 0xFF
    try:
        PacketSerializer.deserialize(bytes(valid))
    except (ValueError, struct.error, UnicodeDecodeError, OSError):
        # Documented failure modes — fine.
        pass


def test_packet_try_deserialize_never_raises_on_garbage() -> None:
    """The lenient ``try_deserialize`` must always return None or a packet,
    never raise — pin that contract on a few adversarial buffers.
    """
    for data in _HAND_PICKED_TOO_SHORT:
        # Should not raise.
        result = PacketSerializer.try_deserialize(data)
        assert result is None


# ─── StoredSignalSession JSON codec round-trip ─────────────────────────

@st.composite
def _stored_signal_sessions(draw: Any) -> StoredSignalSession:
    """Hypothesis strategy producing StoredSignalSession instances."""
    skipped_count = draw(st.integers(min_value=0, max_value=8))
    skipped = {
        f"key{i}": draw(st.binary(min_size=0, max_size=64))
        for i in range(skipped_count)
    }
    return StoredSignalSession(
        root_key=draw(st.binary(min_size=0, max_size=64)),
        send_chain_key=draw(st.one_of(st.none(), st.binary(min_size=0, max_size=64))),
        recv_chain_key=draw(st.one_of(st.none(), st.binary(min_size=0, max_size=64))),
        send_counter=draw(st.integers(min_value=0, max_value=2**31 - 1)),
        recv_counter=draw(st.integers(min_value=0, max_value=2**31 - 1)),
        previous_chain_count=draw(st.integers(min_value=0, max_value=2**31 - 1)),
        my_ephemeral_priv=draw(st.binary(min_size=0, max_size=64)),
        my_ephemeral_pub=draw(st.binary(min_size=0, max_size=64)),
        remote_ephemeral_pub=draw(st.one_of(st.none(), st.binary(min_size=0, max_size=64))),
        skipped_message_keys=skipped,
        pending_pre_key_message=draw(st.booleans()),
        initiator_identity_key_x25519=draw(st.binary(min_size=0, max_size=64)),
        used_signed_pre_key_id=draw(st.integers(min_value=0, max_value=2**31 - 1)),
        used_one_time_pre_key_id=draw(st.integers(min_value=0, max_value=2**31 - 1)),
    )


@_FUZZ
@given(session=_stored_signal_sessions())
def test_stored_signal_session_round_trip(session: StoredSignalSession) -> None:
    """For ANY hypothesis-shaped StoredSignalSession, serialize ->
    deserialize must reproduce all fields exactly.
    """
    blob = serialize_session(session)
    got = deserialize_session(blob)
    assert got is not None
    assert got.root_key == session.root_key
    assert got.send_chain_key == session.send_chain_key
    assert got.recv_chain_key == session.recv_chain_key
    assert got.send_counter == session.send_counter
    assert got.recv_counter == session.recv_counter
    assert got.previous_chain_count == session.previous_chain_count
    assert got.my_ephemeral_priv == session.my_ephemeral_priv
    assert got.my_ephemeral_pub == session.my_ephemeral_pub
    assert got.remote_ephemeral_pub == session.remote_ephemeral_pub
    assert got.skipped_message_keys == session.skipped_message_keys
    assert got.pending_pre_key_message == session.pending_pre_key_message
    assert got.initiator_identity_key_x25519 == session.initiator_identity_key_x25519
    assert got.used_signed_pre_key_id == session.used_signed_pre_key_id
    assert got.used_one_time_pre_key_id == session.used_one_time_pre_key_id


import binascii


# Documented failure modes for the session-store JSON deserializer:
#   - Wire/format failures: ValueError, JSONDecodeError, UnicodeDecodeError, binascii.Error
#   - Shape failures: AttributeError, TypeError (caller hands JSON whose top-level
#     value is not a dict, so ``obj.get(...)`` / int(...) etc. fail mid-parse)
# Anything outside this set escaping = bug, hypothesis will shrink and report.
_SESSION_DESERIALIZER_DOCUMENTED_EXCEPTIONS = (
    ValueError,
    json.JSONDecodeError,
    UnicodeDecodeError,
    binascii.Error,
    AttributeError,
    TypeError,
)


@_FUZZ
@given(data=st.binary(min_size=0, max_size=4096))
def test_stored_signal_session_random_bytes_never_crashes(data: bytes) -> None:
    """Feed random bytes through ``deserialize_session``.

    Contract: only the documented exception types may escape. Anything
    else is a bug — hypothesis will shrink and report.
    """
    try:
        deserialize_session(data)
    except _SESSION_DESERIALIZER_DOCUMENTED_EXCEPTIONS:
        return


@_FUZZ
@given(garbage=st.text(max_size=2048))
def test_stored_signal_session_random_text_never_crashes(garbage: str) -> None:
    """Same contract but for text-shaped bytes — exercises the JSON path
    more aggressively than wholly-random binary.
    """
    try:
        deserialize_session(garbage.encode("utf-8"))
    except _SESSION_DESERIALIZER_DOCUMENTED_EXCEPTIONS:
        return


def test_stored_signal_session_empty_returns_none() -> None:
    """Empty bytes input is the documented "no session" sentinel."""
    assert deserialize_session(b"") is None
