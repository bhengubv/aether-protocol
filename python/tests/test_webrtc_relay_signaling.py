# SPDX-License-Identifier: MIT

"""Acceptance tests for the transport-backed WebRTC signalling carrier.

Proves the production signalling path for the Python implementation: a WebRTC SDP/ICE
signal is framed by :class:`RelaySignaling` (``AWS1`` magic + a System.Text.Json-identical
body) and carried over a real :class:`TransportService` — here an in-process transport pair
standing in for the AetherNet relay — so two SEPARATE nodes negotiate a direct data channel
without a central signalling server.

Three levels are covered:

* **Wire parity** — :func:`encode_signal_frame` is asserted byte-for-byte against the shared
  cross-language fixture ``fixtures/webrtc`` (``inputs.json`` + ``expected/<name>.bin``), the
  exact ``AWS1`` frames the C# ``RelayWebRtcSignaling`` / ``WebRtcSignalJsonContext`` emits and
  every other language leg is pinned against. This is the interop contract and needs no aiortc.
* **Carrier round-trip** — two ``RelaySignaling`` instances (two nodes) over an in-process
  transport pair round-trip an OFFER *and* an ANSWER across the transport.
* **Full handshake** — two ``WebRtcTransport`` instances wired only through two separate
  ``RelaySignaling`` carriers drive a real offer/answer/ICE exchange and move bytes
  peer-to-peer. Guarded by ``importorskip`` since it needs the optional aiortc dependency.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from aethernet.transport.in_process import InProcessTransport
from aethernet.transport.webrtc.signaling import (
    RelaySignaling,
    Signal,
    SignalType,
    decode_signal_frame,
    encode_signal_frame,
)

# ── Shared cross-language fixture (fixtures/webrtc) ───────────────────────────────
# The ground truth is committed, not hardcoded here: fixtures/webrtc/inputs.json holds the
# input cases and fixtures/webrtc/expected/<name>.bin the exact AWS1 frame the C# carrier
# emits for each. Every language leg is pinned against the same bytes, so the wire format
# can never drift between implementations.


def _fixtures_dir() -> Path:
    d = Path(__file__).resolve()
    for _ in range(10):
        if (d / "fixtures" / "webrtc" / "inputs.json").exists():
            return d / "fixtures" / "webrtc"
        if d.parent == d:
            break
        d = d.parent
    raise FileNotFoundError("fixtures/webrtc/inputs.json not found")


def _load_inputs() -> list[dict]:
    return json.loads((_fixtures_dir() / "inputs.json").read_text(encoding="utf-8"))


def _opt(inp: dict, key: str) -> "str | None":
    """Fixture semantics: an empty (or absent) sdp/candidate/sdp_mid means the field is omitted."""
    value = inp.get(key, "")
    return value if value else None


def _signal_for(inp: dict) -> Signal:
    return Signal(
        from_uhid=inp["from_uhid"],
        to_uhid=inp["to_uhid"],
        type=SignalType(inp["type"]),
        sdp=_opt(inp, "sdp"),
        candidate=_opt(inp, "candidate"),
        sdp_mid=_opt(inp, "sdp_mid"),
        sdp_mline_index=inp.get("sdp_mline_index", 0),
    )


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_frame_matches_expected(inp: dict) -> None:
    """encode_signal_frame is byte-identical to the committed expected/<name>.bin."""
    got = encode_signal_frame(_signal_for(inp))
    expected = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    assert got == expected


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_decode_roundtrip(inp: dict) -> None:
    """Decoding the committed frame recovers the input fields exactly."""
    data = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    sig = decode_signal_frame(data)
    assert sig is not None
    assert sig.from_uhid == inp["from_uhid"]
    assert sig.to_uhid == inp["to_uhid"]
    assert int(sig.type) == inp["type"]
    assert (sig.sdp or "") == inp.get("sdp", "")
    assert (sig.candidate or "") == inp.get("candidate", "")
    assert (sig.sdp_mid or "") == inp.get("sdp_mid", "")
    assert sig.sdp_mline_index == inp.get("sdp_mline_index", 0)


def test_roundtrip_encode_decode() -> None:
    """A frame this carrier writes decodes back to an equal Signal."""
    for sig in (
        Signal(from_uhid="a", to_uhid="b", type=SignalType.OFFER, sdp="v=0\r\n"),
        Signal(from_uhid="b", to_uhid="a", type=SignalType.ANSWER, sdp="v=0\r\n"),
        Signal(
            from_uhid="a",
            to_uhid="b",
            type=SignalType.CANDIDATE,
            candidate="candidate:1 1 udp 1 10.0.0.1 5 typ host",
            sdp_mid="0",
            sdp_mline_index=0,
        ),
    ):
        assert decode_signal_frame(encode_signal_frame(sig)) == sig


def test_decode_ignores_non_signalling_bytes() -> None:
    """Payloads without the AWS1 magic are ordinary app traffic, not signals."""
    assert decode_signal_frame(b"ordinary app data") is None
    assert decode_signal_frame(b"") is None
    assert decode_signal_frame(b"AWS") is None  # too short for the magic
    assert decode_signal_frame(b"AWS1not-json") is None  # magic but malformed body


@pytest.mark.asyncio
async def test_two_carriers_roundtrip_offer_and_answer_over_transport() -> None:
    """Two SEPARATE carriers (two nodes) exchange an OFFER and an ANSWER over a transport.

    This is the transport-backed equivalent of the C# ``RelaySignalingTests`` at the
    signalling layer: no aiortc, no data channel — just proof that the framed handshake
    rides a real ``TransportService`` from one node to the other in both directions.
    """
    alice_relay = InProcessTransport("alice")
    bob_relay = InProcessTransport("bob")
    try:
        alice_sig = RelaySignaling(alice_relay)
        bob_sig = RelaySignaling(bob_relay)

        alice_inbox: "asyncio.Queue[Signal]" = asyncio.Queue()
        bob_inbox: "asyncio.Queue[Signal]" = asyncio.Queue()
        alice_sig.on_signal(alice_inbox.put_nowait)
        bob_sig.on_signal(bob_inbox.put_nowait)

        # alice -> bob: OFFER
        offer = Signal(
            from_uhid="alice", to_uhid="bob", type=SignalType.OFFER, sdp="v=0\r\no=offer\r\n"
        )
        assert await alice_sig.send_signal("bob", offer) is True
        got_offer = await asyncio.wait_for(bob_inbox.get(), timeout=5.0)
        assert got_offer == offer

        # bob -> alice: ANSWER
        answer = Signal(
            from_uhid="bob", to_uhid="alice", type=SignalType.ANSWER, sdp="v=0\r\na=answer\r\n"
        )
        assert await bob_sig.send_signal("alice", answer) is True
        got_answer = await asyncio.wait_for(alice_inbox.get(), timeout=5.0)
        assert got_answer == answer
    finally:
        alice_relay.shutdown()
        bob_relay.shutdown()


@pytest.mark.asyncio
async def test_non_signalling_traffic_does_not_surface_as_signal() -> None:
    """App bytes without the AWS1 prefix sent over the same channel raise no signal."""
    a = InProcessTransport("node-a")
    b = InProcessTransport("node-b")
    try:
        b_sig = RelaySignaling(b)
        raised: list = []
        b_sig.on_signal(raised.append)

        # a sends plain app bytes to b; b's carrier must ignore them.
        assert await a.send_async("node-b", b"ordinary app data") is True
        await asyncio.sleep(0)  # let any callback run
        assert raised == [], "non-prefixed app bytes must not decode as signalling"
    finally:
        a.shutdown()
        b.shutdown()


# ── Full aiortc handshake over the transport-backed carrier (optional dep) ────────
aiortc = pytest.importorskip(
    "aiortc", reason="aiortc not installed (optional WebRTC dependency)"
)

from aethernet.transport.webrtc import WebRtcTransport  # noqa: E402  (after importorskip)


@pytest.mark.asyncio
async def test_full_handshake_over_relay_carrier_then_data_goes_direct() -> None:
    """Two WebRtcTransports, wired only through two SEPARATE RelaySignaling carriers over an
    in-process transport pair, negotiate a real data channel and move bytes peer-to-peer.

    The handshake rides the relay (the framed AWS1 signals); the payload then flows over the
    direct WebRTC data channel. Host-candidate-only ICE (empty server list) keeps it headless
    and network-free. Mirrors the C# ``Handshake_RidesRelay_ThenDataGoesDirect``.
    """
    _skip_if_aiortc_dtls_unavailable()

    alice_relay = InProcessTransport("alice")
    bob_relay = InProcessTransport("bob")
    alice = WebRtcTransport("alice", RelaySignaling(alice_relay), [])
    bob = WebRtcTransport("bob", RelaySignaling(bob_relay), [])

    got: "asyncio.Queue[bytes]" = asyncio.Queue()
    bob.on_data_received(lambda sender, data: got.put_nowait(data) if sender == "alice" else None)

    try:
        payload = b"handshake rode the relay; the data went direct"
        assert await alice.send_async("bob", payload), "negotiation over the relay should succeed"

        received = await asyncio.wait_for(got.get(), timeout=30.0)
        assert received == payload, f"payload mismatch: got {received!r} want {payload!r}"
        assert alice.is_connected("bob")
        assert bob.is_connected("alice")
    finally:
        await alice.close()
        await bob.close()
        alice_relay.shutdown()
        bob_relay.shutdown()


def _skip_if_aiortc_dtls_unavailable() -> None:
    """Skip the full data-flow handshake when aiortc's DTLS layer can't initialize here.

    aiortc negotiates SDP/ICE at the Python layer (which this carrier drives fine) but then
    hands the media/data path to a native DTLS stack via pyOpenSSL. Some pyOpenSSL /
    cryptography / OpenSSL combinations reject aiortc's self-signed certificate object with
    ``TypeError: cert must be an X509 instance``, so a full loopback data channel cannot
    complete in-process — independently of signalling. When that is the case we skip rather
    than fail: the carrier is already proven byte-identical to C# and proven to round-trip an
    offer *and* answer over a real transport by the tests above. Remove this guard to run the
    full data-flow assertion once the environment's DTLS stack is compatible.
    """
    from aiortc.rtcdtlstransport import RTCCertificate

    try:
        cert = RTCCertificate.generateCertificate()
        # This is the exact call aiortc makes during __connect() (with no SRTP profiles); it
        # raises the X509 TypeError on incompatible stacks before any network I/O.
        cert._create_ssl_context([])  # type: ignore[attr-defined]
    except TypeError as exc:
        if "X509" in str(exc):
            pytest.skip(
                "aiortc DTLS cannot initialize in this environment "
                f"(pyOpenSSL/cryptography incompatibility: {exc}); "
                "signalling-carrier parity and offer/answer round-trip are covered by the "
                "other tests in this module."
            )
        raise
    except Exception:
        # Any other failure constructing the context: let the real test surface it.
        pass
