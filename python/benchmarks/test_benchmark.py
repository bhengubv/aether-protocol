# SPDX-License-Identifier: MIT

"""pytest-benchmark harness for the Python aether-protocol hot paths.

Mirrors the C# AetherMesh.Benchmarks suite and the Go ``go/bench`` harness —
the same hot paths so a regression in any language shows up as a delta
against the committed baseline. Eleven benchmark cases:

* ``bench_x25519_agree`` — one ECDH agreement (X3DH inner loop).
* ``bench_hkdf_sha256_64bytes`` — KDF_RK (Signal §5.2) per ratchet step.
* ``bench_x3dh_establish`` — full pre-key bundle process; 4 X25519 + HKDF.
* ``bench_signal_encrypt`` — steady-state Encrypt path; HMAC chain + AES-GCM.
* ``bench_signal_decrypt`` — steady-state Decrypt path.
* ``bench_packet_serialize`` — wire serialiser on a 50-byte payload.
* ``bench_packet_serialize_large`` — wire serialiser on a 10KB payload.
* ``bench_packet_deserialize`` — wire deserialiser.
* ``bench_packet_round_trip`` — single-number regression detector.
* ``bench_route_store_lookup`` — cached-route hot path.
* ``bench_route_store_save`` — install a new route entry.

Run from ``python/``::

    python -m pytest benchmarks/ --benchmark-only -q

Pin a baseline::

    python -m pytest benchmarks/ --benchmark-only \
        --benchmark-save=python_baseline -q

Compare a future run::

    python -m pytest benchmarks/ --benchmark-only \
        --benchmark-compare=python_baseline -q

The benches only call exported APIs from :mod:`aethermesh.security`,
:mod:`aethermesh.protocol`, and :mod:`aethermesh.routing`; lower-level
primitives (X25519, HKDF) come from :mod:`cryptography.hazmat`, the same
library the production code uses, so the numbers are directly comparable
to the C# / Go runs.
"""

from __future__ import annotations

import asyncio
import os
import time
from datetime import datetime, timedelta
from typing import Tuple
from uuid import uuid4

import pytest
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.serialization import (
    Encoding,
    NoEncryption,
    PrivateFormat,
    PublicFormat,
)

from aethermesh.models import RouteEntry
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer
from aethermesh.routing.store import InMemoryRouteStore
from aethermesh.security.signal_protocol import SignalProtocolService


_ALICE = "alice-uhid"
_BOB = "bob-uhid"
_PLAINTEXT_SMALL = b"hello, mesh"


# ─── Async event-loop helper ───────────────────────────────────────────

def _run(coro):
    """Drive an awaitable to completion on a fresh event loop.

    pytest-benchmark wraps the function under measurement in a tight
    loop; spinning a fresh loop per iteration would dominate the
    measurement, so each benchmark function below caches a long-lived
    loop in module scope where appropriate.
    """
    return asyncio.get_event_loop().run_until_complete(coro)


@pytest.fixture(scope="module")
def event_loop():
    """Module-scoped event loop so async fixtures share one across the
    benchmark sweep.
    """
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    yield loop
    loop.close()


# ─── X25519 ECDH ───────────────────────────────────────────────────────

def test_bench_x25519_agree(benchmark) -> None:
    """Pin a baseline for one ECDH agreement — the inner-loop primitive
    of X3DH (4x per session establishment) and DH-ratchet (2x per
    ratchet step).
    """
    priv = X25519PrivateKey.generate()
    peer_priv = X25519PrivateKey.generate()
    peer_pub = peer_priv.public_key()

    benchmark(lambda: priv.exchange(peer_pub))


# ─── HKDF-SHA256 ───────────────────────────────────────────────────────

def test_bench_hkdf_sha256_64bytes(benchmark) -> None:
    """Pin KDF_RK per Signal §5.2 — 32-byte new root + 32-byte new chain
    = 64 bytes out, called once per DH-ratchet step.
    """
    ikm = os.urandom(32)
    salt = os.urandom(32)
    info = b"aether-ratchet-rk-v1"

    def _do() -> bytes:
        hkdf = HKDF(
            algorithm=hashes.SHA256(),
            length=64,
            salt=salt,
            info=info,
        )
        return hkdf.derive(ikm)

    benchmark(_do)


# ─── X3DH establishment ────────────────────────────────────────────────

def test_bench_x3dh_establish(benchmark, event_loop) -> None:
    """Pin the cost of a full pre-key bundle process — 4 X25519
    agreements + HKDF root derivation. One-shot per peer.

    Each round uses a fresh initiator (so the session-state dictionary
    doesn't grow unbounded) and a fresh bundle (so an OPK is consumed
    per round and Bob's pool is exercised). The bundle / initiator
    construction itself is excluded from the timed portion via
    pytest-benchmark's setup_func + pedantic mode.
    """
    bob = SignalProtocolService()
    bob_uhid = _BOB

    def _setup() -> Tuple[Tuple[SignalProtocolService, object], dict]:
        alice = SignalProtocolService()
        event_loop.run_until_complete(alice.generate_pre_key_bundle(_ALICE))
        bundle = event_loop.run_until_complete(bob.generate_pre_key_bundle(bob_uhid))
        return ((alice, bundle), {})

    def _run_once(alice: SignalProtocolService, bundle) -> None:
        event_loop.run_until_complete(alice.process_pre_key_bundle(bundle))

    benchmark.pedantic(_run_once, setup=_setup, rounds=20, iterations=1)


# ─── Signal encrypt / decrypt ──────────────────────────────────────────

def _warmed_pair(loop) -> Tuple[SignalProtocolService, SignalProtocolService]:
    """Build an Alice/Bob pair with a fully-primed Double Ratchet so the
    bench measures the steady-state chain step rather than the one-shot
    X3DH cost.
    """
    alice = SignalProtocolService()
    bob = SignalProtocolService()
    loop.run_until_complete(alice.generate_pre_key_bundle(_ALICE))
    bob_bundle = loop.run_until_complete(bob.generate_pre_key_bundle(_BOB))
    loop.run_until_complete(alice.process_pre_key_bundle(bob_bundle))
    first = loop.run_until_complete(alice.encrypt(_BOB, _PLAINTEXT_SMALL))
    loop.run_until_complete(bob.decrypt(_ALICE, first))
    return alice, bob


def test_bench_signal_encrypt(benchmark, event_loop) -> None:
    """Pin the steady-state Encrypt path — 1 HMAC chain step +
    AES-GCM. Excludes the one-shot X3DH cost by warming the session.
    """
    alice, _bob = _warmed_pair(event_loop)
    plaintext = os.urandom(256)

    def _do() -> None:
        event_loop.run_until_complete(alice.encrypt(_BOB, plaintext))

    benchmark(_do)


def test_bench_signal_decrypt(benchmark, event_loop) -> None:
    """Pin the steady-state Decrypt path. Each iteration must consume
    a freshly-encrypted payload (the receive ratchet advances, so
    re-decrypting the same bytes is not allowed). Encrypt-side setup
    is excluded via pedantic setup_func.
    """
    alice, bob = _warmed_pair(event_loop)
    plaintext = os.urandom(256)

    def _setup() -> Tuple[tuple, dict]:
        payload = event_loop.run_until_complete(alice.encrypt(_BOB, plaintext))
        return ((payload,), {})

    def _do(payload) -> None:
        event_loop.run_until_complete(bob.decrypt(_ALICE, payload))

    benchmark.pedantic(_do, setup=_setup, rounds=200, iterations=1)


# ─── Wire-format serializer ────────────────────────────────────────────

def _make_packet(payload_size: int) -> MeshPacket:
    """Build a representative MeshPacket for benchmarking."""
    return MeshPacket(
        id=uuid4(),
        type=PacketType.Data,
        source_uhid="alice-uhid-0001",
        destination_uhid="bob-uhid-0002",
        ttl=7,
        priority=1,
        protocol_version=2,
        timestamp_ms=int(time.time() * 1000),
        packet_nonce=os.urandom(8),
        payload=os.urandom(payload_size),
        signature=os.urandom(64),
    )


def test_bench_packet_serialize(benchmark) -> None:
    """Pin Serialize on a representative 50-byte Data packet.

    Every packet on the mesh runs through this on send.
    """
    pkt = _make_packet(50)
    benchmark(lambda: PacketSerializer.serialize(pkt))


def test_bench_packet_serialize_large(benchmark) -> None:
    """Pin Serialize on a 10 KB payload (typical chunked-data or
    video-frame packet).
    """
    pkt = _make_packet(10240)
    benchmark(lambda: PacketSerializer.serialize(pkt))


def test_bench_packet_deserialize(benchmark) -> None:
    """Pin Deserialize on a representative wire envelope.

    Every hop runs this on receive; a regression multiplies across
    every router.
    """
    wire = PacketSerializer.serialize(_make_packet(50))
    benchmark(lambda: PacketSerializer.deserialize(wire))


def test_bench_packet_round_trip(benchmark) -> None:
    """Combined Serialize + Deserialize — single-number regression
    detector that catches changes in either side.
    """
    pkt = _make_packet(50)

    def _do() -> None:
        wire = PacketSerializer.serialize(pkt)
        got = PacketSerializer.deserialize(wire)
        # Defeat dead-store elimination — touch a field so the runtime
        # doesn't optimise the deserialize away.
        if got is None or not got.source_uhid:
            raise RuntimeError("unexpected nil/empty packet")

    benchmark(_do)


# ─── Routing ───────────────────────────────────────────────────────────

def test_bench_route_store_lookup(benchmark, event_loop) -> None:
    """Pin the cached-route hot path — the steady state for every
    outbound packet that already has a route.
    """
    store = InMemoryRouteStore()
    entry = RouteEntry(
        destination_uhid=_BOB,
        next_hop_uhid="relay-uhid",
        hop_count=2,
        expires_at=datetime.utcnow() + timedelta(hours=1),
        quality_score=90,
    )
    event_loop.run_until_complete(store.save(entry))

    def _do() -> None:
        got = event_loop.run_until_complete(store.get(_BOB))
        if got is None:
            raise RuntimeError("expected cached route")

    benchmark(_do)


def test_bench_route_store_save(benchmark, event_loop) -> None:
    """Pin the cost of installing a new route entry — what happens on
    every successful RREP arrival.
    """
    store = InMemoryRouteStore()
    expires = datetime.utcnow() + timedelta(hours=1)

    def _do() -> None:
        entry = RouteEntry(
            destination_uhid="dest",
            next_hop_uhid="hop",
            hop_count=1,
            expires_at=expires,
            quality_score=100,
        )
        event_loop.run_until_complete(store.save(entry))

    benchmark(_do)
