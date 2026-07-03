# SPDX-License-Identifier: MIT

"""WebRTC signalling abstraction and the in-process reference bus.

A :class:`Signal` is one SDP offer/answer or trickled ICE candidate that two peers
must exchange before a direct ``RTCDataChannel`` can open. :class:`Signaling` carries
those signals between peers by UHID, so the handshake never needs a central signalling
server — any already-reachable channel (the relay, the radio mesh, an SMS ignition link)
can back it. :class:`InMemorySignalingBus` is the reference implementation: it routes
signals in process, in send order, on each endpoint's own task, mirroring the C# and Go
buses.
"""

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import TYPE_CHECKING, Callable, Dict, List, Optional

if TYPE_CHECKING:  # avoid a runtime import cycle (transport_service is a sibling package member)
    from aethernet.transport.transport_service import TransportService


class SignalType(IntEnum):
    """The kind of WebRTC signalling message exchanged while a direct link is set up."""

    OFFER = 0
    """SDP offer from the initiating peer."""

    ANSWER = 1
    """SDP answer from the responding peer."""

    CANDIDATE = 2
    """A trickled ICE candidate."""


@dataclass(frozen=True)
class Signal:
    """A single WebRTC signalling message.

    Carries either the SDP offer/answer (``OFFER`` / ``ANSWER``) or a trickled ICE
    candidate (``CANDIDATE``) that two peers exchange before a direct data channel opens.
    """

    from_uhid: str
    """UHID of the node that produced this signal."""

    to_uhid: str
    """UHID of the node this signal is addressed to."""

    type: SignalType
    """What this signal carries."""

    sdp: Optional[str] = None
    """The SDP text — set for ``OFFER`` / ``ANSWER``."""

    candidate: Optional[str] = None
    """The ICE candidate string — set for ``CANDIDATE``."""

    sdp_mid: Optional[str] = None
    """The SDP mid for the ICE candidate."""

    sdp_mline_index: int = 0
    """The SDP m-line index for the ICE candidate (0 for the single data section)."""


class Signaling(ABC):
    """Carries WebRTC SDP/ICE signalling between two peers by UHID.

    Any already-reachable channel can back this — the AetherNet relay, the radio mesh,
    or an SMS ignition link for cold first contact — so a direct data channel can be
    negotiated without a central signalling server.
    """

    @abstractmethod
    async def send_signal(self, peer_uhid: str, signal: Signal) -> bool:
        """Deliver a signalling message to ``peer_uhid``.

        Returns:
            True if the signal was handed to the underlying channel, False otherwise.
        """

    @abstractmethod
    def on_signal(self, handler: Callable[[Signal], None]) -> None:
        """Register the handler invoked for signals addressed to the local node."""


class InMemorySignalingBus:
    """In-process :class:`Signaling` bus that routes signals between endpoints by UHID.

    The reference signalling implementation: it needs no network and no server, so it
    backs same-process scenarios (multi-node simulations, a single device holding several
    identities) and the test suite. Production cross-device signalling rides a real
    transport instead.

    Each endpoint delivers inbound signals on its own single-reader queue, so signals
    arrive in send order and never re-enter the sender's call stack — matching the
    ordered, reliable delivery a real signalling channel provides.
    """

    def __init__(self) -> None:
        self._endpoints: Dict[str, "_Endpoint"] = {}

    def endpoint(self, uhid: str) -> Signaling:
        """Return the signalling endpoint for ``uhid``, creating it once."""
        existing = self._endpoints.get(uhid)
        if existing is not None:
            return existing
        ep = _Endpoint(self)
        self._endpoints[uhid] = ep
        return ep

    async def close(self) -> None:
        """Stop all endpoint pumps."""
        endpoints = list(self._endpoints.values())
        self._endpoints.clear()
        for ep in endpoints:
            await ep.close()

    async def _route(self, signal: Signal) -> bool:
        target = self._endpoints.get(signal.to_uhid)
        if target is None:
            return False
        return target.deliver(signal)


class _Endpoint(Signaling):
    """One bus endpoint: a single-reader queue plus a pump task delivering in send order."""

    def __init__(self, bus: InMemorySignalingBus) -> None:
        self._bus = bus
        self._inbox: "asyncio.Queue[Signal]" = asyncio.Queue()
        self._handler: Optional[Callable[[Signal], None]] = None
        self._closed = False
        self._pump = asyncio.ensure_future(self._run())

    async def send_signal(self, peer_uhid: str, signal: Signal) -> bool:
        return await self._bus._route(signal)

    def on_signal(self, handler: Callable[[Signal], None]) -> None:
        self._handler = handler

    def deliver(self, signal: Signal) -> bool:
        if self._closed:
            return False
        self._inbox.put_nowait(signal)
        return True

    async def _run(self) -> None:
        while True:
            signal = await self._inbox.get()
            if signal is _SHUTDOWN:  # type: ignore[comparison-overlap]
                return
            handler = self._handler
            if handler is not None:
                try:
                    handler(signal)
                except Exception:
                    # A misbehaving handler must not stop the queue.
                    pass

    async def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._inbox.put_nowait(_SHUTDOWN)  # type: ignore[arg-type]
        try:
            await self._pump
        except Exception:
            # Pump teardown is best-effort.
            pass


# Sentinel pushed onto an endpoint's queue to end its pump task cleanly.
_SHUTDOWN: Signal = Signal(from_uhid="", to_uhid="", type=SignalType.OFFER)


# ─────────────────────────────────────────────────────────────────────────────
# Transport-backed carrier: rides a real TransportService (the relay / mesh / an
# in-process pair) so two SEPARATE nodes exchange the SDP/ICE handshake without a
# central signalling server. The frame is byte-identical to the C#
# RelayWebRtcSignaling (AWS1 magic + System.Text.Json body), so a Python node and a
# C# node interoperate on the wire.
# ─────────────────────────────────────────────────────────────────────────────

# "AWS1" = Aether WebRtc Signal, framing v1 — the same 4-byte magic C# writes.
_MAGIC: bytes = b"AWS1"

# System.Text.Json's default JavaScriptEncoder leaves these ASCII code points (0x20-0x7E)
# unescaped; every other char (the rest of ASCII punctuation, all control chars, and all
# non-ASCII) is escaped. Verified empirically against the C# WebRtcSignalJsonContext:
#   unescaped = space ! # $ % ( ) * , - . / 0-9 : ; = ? @ A-Z [ ] ^ _ a-z { | } ~
# Note the deliberate exclusions vs a naive json.dumps: " & ' + < > ` are escaped as
# \uXXXX (uppercase), and `/` is NOT escaped. Matching this set exactly is what makes the
# body byte-identical to C#.
_STJ_UNESCAPED: frozenset = frozenset(
    " !#$%()*,-./0123456789:;=?@"
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_"
    "abcdefghijklmnopqrstuvwxyz{|}~"
)

# The five whitespace controls STJ renders with a short escape; everything else that must
# be escaped uses \uXXXX (uppercase). Backslash also gets the short "\\" form.
_STJ_SHORT_ESCAPES: Dict[str, str] = {
    "\\": "\\\\",
    "\b": "\\b",
    "\f": "\\f",
    "\n": "\\n",
    "\r": "\\r",
    "\t": "\\t",
}


def _stj_escape(value: str) -> str:
    """Escape ``value`` exactly as System.Text.Json's default encoder does.

    This is the byte-for-byte contract with the C# carrier: the same characters escaped,
    the same short-vs-\\uXXXX choice, and uppercase hex — so the resulting frame body is
    identical to what ``RelayWebRtcSignaling`` puts on the wire.
    """
    out: List[str] = []
    for ch in value:
        short = _STJ_SHORT_ESCAPES.get(ch)
        if short is not None:
            out.append(short)
        elif ch in _STJ_UNESCAPED:
            out.append(ch)
        else:
            # Escape per UTF-16 code unit (astral chars -> surrogate pair, each \uXXXX),
            # matching STJ which operates on UTF-16. ord() on a Python str char is the
            # code point; encode to UTF-16-BE to get its code unit(s).
            for unit in value_utf16_units(ch):
                out.append(f"\\u{unit:04X}")
    return "".join(out)


def value_utf16_units(ch: str) -> List[int]:
    """Return the UTF-16 code unit(s) for a single character (surrogate pair if astral)."""
    code = ord(ch)
    if code <= 0xFFFF:
        return [code]
    code -= 0x10000
    high = 0xD800 + (code >> 10)
    low = 0xDC00 + (code & 0x3FF)
    return [high, low]


def encode_signal_frame(signal: Signal) -> bytes:
    """Serialize ``signal`` into the on-wire AWS1 frame, byte-identical to C#.

    Body layout (C# property order, System.Text.Json, ``WhenWritingNull``):
    ``{"FromUhid":..,"ToUhid":..,"Type":<int>,"Sdp":..?,"Candidate":..?,
    "SdpMLineIndex":<int>,"SdpMid":..?}`` — nulls omitted, ``SdpMLineIndex`` always
    present (it is a non-nullable ``ushort`` in C#), compact separators, no spaces.
    """
    parts: List[str] = []
    parts.append('"FromUhid":"' + _stj_escape(signal.from_uhid) + '"')
    parts.append('"ToUhid":"' + _stj_escape(signal.to_uhid) + '"')
    parts.append('"Type":' + str(int(signal.type)))
    if signal.sdp is not None:
        parts.append('"Sdp":"' + _stj_escape(signal.sdp) + '"')
    if signal.candidate is not None:
        parts.append('"Candidate":"' + _stj_escape(signal.candidate) + '"')
    parts.append('"SdpMLineIndex":' + str(int(signal.sdp_mline_index)))
    if signal.sdp_mid is not None:
        parts.append('"SdpMid":"' + _stj_escape(signal.sdp_mid) + '"')
    body = ("{" + ",".join(parts) + "}").encode("utf-8")
    return _MAGIC + body


def decode_signal_frame(data: bytes) -> Optional[Signal]:
    """Parse an AWS1 frame back into a :class:`Signal`, or ``None`` if it is not one.

    Returns ``None`` for any payload lacking the AWS1 magic (ordinary app traffic) or whose
    body is malformed — mirroring the C# carrier, which silently ignores non-signalling bytes.
    The JSON is read case-tolerantly on the C# PascalCase keys (and the Python snake_case /
    Go compact keys) so a frame written by any peer is understood.
    """
    if len(data) < len(_MAGIC) or not data.startswith(_MAGIC):
        return None
    import json

    try:
        obj = json.loads(bytes(data[len(_MAGIC):]).decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None
    if not isinstance(obj, dict):
        return None

    def pick(*keys: str) -> Optional[object]:
        for k in keys:
            if k in obj:
                return obj[k]
        return None

    from_uhid = pick("FromUhid", "from_uhid", "from")
    to_uhid = pick("ToUhid", "to_uhid", "to")
    type_raw = pick("Type", "type")
    if from_uhid is None or to_uhid is None or type_raw is None:
        return None
    try:
        sig_type = SignalType(int(type_raw))
    except (ValueError, TypeError):
        return None

    mline_raw = pick("SdpMLineIndex", "sdp_mline_index", "mline")
    try:
        mline = int(mline_raw) if mline_raw is not None else 0
    except (ValueError, TypeError):
        mline = 0

    sdp = pick("Sdp", "sdp")
    candidate = pick("Candidate", "candidate")
    sdp_mid = pick("SdpMid", "sdp_mid", "mid")
    return Signal(
        from_uhid=str(from_uhid),
        to_uhid=str(to_uhid),
        type=sig_type,
        sdp=str(sdp) if sdp is not None else None,
        candidate=str(candidate) if candidate is not None else None,
        sdp_mid=str(sdp_mid) if sdp_mid is not None else None,
        sdp_mline_index=mline,
    )


class RelaySignaling(Signaling):
    """Carries WebRTC SDP/ICE signalling over an existing :class:`TransportService`.

    The transport-backed counterpart to :class:`InMemorySignalingBus`: instead of routing in
    process, it frames each :class:`Signal` (``AWS1`` magic + a System.Text.Json-identical
    body) and hands it to a real transport channel — the AetherNet relay, the radio mesh, or
    an in-process pair for tests. Inbound bytes on that channel that lack the ``AWS1`` magic
    are ignored: they are ordinary application traffic, not signalling.

    Give this a channel whose received-data callback is dedicated to signalling (e.g. a relay
    connection reserved for control traffic), so the prefixed control frames never reach the
    application data path. This mirrors the C# ``RelayWebRtcSignaling`` exactly, so a Python
    node and a C# node negotiate a WebRTC link over the wire.
    """

    def __init__(self, channel: "TransportService") -> None:
        """Wrap ``channel`` as a signalling carrier and subscribe to its inbound bytes."""
        if channel is None:
            raise ValueError("webrtc signaling: channel required")
        self._channel = channel
        self._handler: Optional[Callable[[Signal], None]] = None
        channel.on_data_received(self._on_channel_data)

    async def send_signal(self, peer_uhid: str, signal: Signal) -> bool:
        """Frame ``signal`` and send it to ``peer_uhid`` over the underlying transport."""
        frame = encode_signal_frame(signal)
        return await self._channel.send_async(peer_uhid, frame)

    def on_signal(self, handler: Callable[[Signal], None]) -> None:
        """Register the handler invoked for signalling frames arriving on the channel."""
        self._handler = handler

    def _on_channel_data(self, from_uhid: str, data: bytes) -> None:
        signal = decode_signal_frame(data)
        if signal is None:
            return  # ordinary app traffic, not a signalling frame
        handler = self._handler
        if handler is not None:
            try:
                handler(signal)
            except Exception:
                # A misbehaving handler must not break the transport's receive path.
                pass
