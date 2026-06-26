# SPDX-License-Identifier: MIT

"""Loopback tests for aethernet.transport.webrtc.WebRtcTransport.

Stands up two real ``WebRtcTransport`` instances wired only through an in-process
signalling bus — no central server, no STUN — and proves a direct data channel negotiates
over host candidates and carries bytes. Mirrors the Go ``TestTwoPeersExchangeBytesNoServer``
and the C# loopback test.
"""

from __future__ import annotations

import asyncio

import pytest

# The WebRTC transport requires aiortc, an optional heavy native dependency. Skip
# this whole module gracefully when it is absent rather than erroring out
# collection for the entire suite.
pytest.importorskip("aiortc", reason="aiortc not installed (optional WebRTC dependency)")

from aethernet.transport.webrtc import InMemorySignalingBus, WebRtcTransport


@pytest.mark.asyncio
async def test_two_peers_exchange_bytes_no_server() -> None:
    """Two transports negotiate a data channel over host-only ICE and exchange bytes."""
    bus = InMemorySignalingBus()

    # Empty (not None) => host-candidate-only ICE, no network dependency.
    host_only: list = []

    alice = WebRtcTransport("alice", bus.endpoint("alice"), host_only)
    bob = WebRtcTransport("bob", bus.endpoint("bob"), host_only)

    got: "asyncio.Queue[bytes]" = asyncio.Queue()

    def on_bob_data(sender: str, data: bytes) -> None:
        if sender == "alice":
            got.put_nowait(data)

    bob.on_data_received(on_bob_data)

    try:
        payload = b"hello over a serverless webrtc datachannel"
        ok = await alice.send_async("bob", payload)
        assert ok, "alice.send_async should report success"

        received = await asyncio.wait_for(got.get(), timeout=30.0)
        assert received == payload, f"payload mismatch: got {received!r} want {payload!r}"

        assert alice.is_connected("bob"), "alice should report connected to bob"
        assert bob.is_connected("alice"), "bob should report connected to alice"
    finally:
        await alice.close()
        await bob.close()
        await bus.close()


@pytest.mark.asyncio
async def test_bidirectional_exchange() -> None:
    """Both directions carry bytes once the single negotiated channel is open."""
    bus = InMemorySignalingBus()
    alice = WebRtcTransport("alice", bus.endpoint("alice"), [])
    bob = WebRtcTransport("bob", bus.endpoint("bob"), [])

    to_bob: "asyncio.Queue[bytes]" = asyncio.Queue()
    to_alice: "asyncio.Queue[bytes]" = asyncio.Queue()
    bob.on_data_received(lambda s, d: to_bob.put_nowait(d))
    alice.on_data_received(lambda s, d: to_alice.put_nowait(d))

    try:
        assert await alice.send_async("bob", b"ping")
        assert await asyncio.wait_for(to_bob.get(), timeout=30.0) == b"ping"

        # Reuse the established link in the reverse direction.
        assert await bob.send_async("alice", b"pong")
        assert await asyncio.wait_for(to_alice.get(), timeout=30.0) == b"pong"
    finally:
        await alice.close()
        await bob.close()
        await bus.close()


@pytest.mark.asyncio
async def test_transport_metadata() -> None:
    """The ladder-facing metadata matches the C#/Go reference."""
    bus = InMemorySignalingBus()
    tr = WebRtcTransport("x", bus.endpoint("x"), [])
    try:
        assert tr.name == "WebRTC P2P"
        assert tr.is_available is True
        assert tr.max_range_meters == 0  # internet — unbounded
        assert tr.max_bandwidth_bps > 0
        assert tr.metrics is not None
    finally:
        await tr.close()
        await bus.close()


@pytest.mark.asyncio
async def test_send_to_unknown_peer_returns_false() -> None:
    """A send to a peer the bus cannot route fails fast rather than hanging forever."""
    bus = InMemorySignalingBus()
    alice = WebRtcTransport("alice", bus.endpoint("alice"), [])
    try:
        # "ghost" has no endpoint; the offer is dropped, so the channel never opens and the
        # connect timeout governs. Use a short patch-free wait by relying on the 20s internal
        # timeout being well under any test runner deadline is undesirable, so assert the
        # negative result the handshake yields once it cannot complete.
        ok = await asyncio.wait_for(alice.send_async("ghost", b"\x01"), timeout=25.0)
        assert ok is False
    finally:
        await alice.close()
        await bus.close()
