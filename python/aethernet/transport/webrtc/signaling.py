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
from typing import Callable, Dict, Optional


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
