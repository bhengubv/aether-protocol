# SPDX-License-Identifier: MIT

"""Direct peer-to-peer transport for AetherNet over a WebRTC data channel (aiortc).

Serverless by default: with the default (no ICE servers) a node never contacts a STUN/TURN
server — host-candidate-only ICE forms a direct link on the same LAN or when a peer has a public
address. STUN/TURN are OPTIONAL (opted into by passing an explicit ICE-server list) and help
traverse NATs that host candidates alone can't. The initial SDP/ICE handshake is carried by an
injected :class:`~aethernet.transport.webrtc.signaling.Signaling`
channel (e.g. the AetherNet relay), so no central signalling server is required either. The class
implements :class:`~aethernet.transport.transport_service.TransportService`, so the transport
selector ranks it between the radio mesh (cheap, proximity) and the QUIC/HTTP relay (last
resort): a direct internet path is used when one can be negotiated, otherwise the relay
carries the traffic.

This is the first real, internet-capable transport for the Python implementation — the
others (e.g. ``InProcessTransport``) are in-process simulations.
"""

from __future__ import annotations

import asyncio
from typing import Callable, Dict, List, Optional

from aiortc import (
    RTCConfiguration,
    RTCDataChannel,
    RTCIceCandidate,
    RTCIceServer,
    RTCPeerConnection,
    RTCSessionDescription,
)
from aiortc.sdp import candidate_from_sdp, candidate_to_sdp

from aethernet.transport.per_transport_metrics import PerTransportMetrics
from aethernet.transport.transport_service import TransportService
from aethernet.transport.webrtc.signaling import Signal, Signaling, SignalType

_DATA_CHANNEL_LABEL = "aether"
_CONNECT_TIMEOUT_SECONDS = 20.0


def default_ice_servers() -> List[RTCIceServer]:
    """Serverless default: NO ICE servers, so a node never contacts a STUN/TURN server.

    Direct links form on the same LAN or when a peer has a public address; for NAT traversal
    without a server, route through the circuit-relay-v2 transport (peers relay for peers).
    Callers opt into STUN/TURN by passing an explicit list.
    """
    return []


class WebRtcTransport(TransportService):
    """A :class:`TransportService` over a direct WebRTC data channel (aiortc)."""

    def __init__(
        self,
        local_uhid: str,
        signaling: Signaling,
        ice_servers: Optional[List[RTCIceServer]] = None,
    ) -> None:
        """Build a transport for ``local_uhid``.

        Args:
            local_uhid: This node's UHID.
            signaling: The channel that carries SDP/ICE signalling to peers by UHID.
            ice_servers: ``None`` selects the serverless default of NO ICE servers
                (host-candidate-only ICE; never contacts a STUN/TURN server, links form
                on the same LAN or when a peer has a public address). For NAT traversal
                without a server, route through the circuit-relay-v2 transport (peers
                relay for peers). An explicit list is respected verbatim, so a caller can
                opt into STUN/TURN, or pass an empty list to keep host-candidate-only ICE
                (e.g. same-LAN / tests).
        """
        if not local_uhid:
            raise ValueError("webrtc: local_uhid required")
        if signaling is None:
            raise ValueError("webrtc: signaling required")

        self._local_uhid = local_uhid
        self._signaling = signaling
        # None => the serverless default (NO ICE servers); an explicit (even empty) list is respected verbatim.
        self._ice_servers = default_ice_servers() if ice_servers is None else list(ice_servers)
        self._metrics = PerTransportMetrics()
        self._on_data: Optional[Callable[[str, bytes], None]] = None
        self._peers: Dict[str, "_PeerLink"] = {}
        self._lock = asyncio.Lock()
        self._closed = False

        signaling.on_signal(self._handle_signal)

    # ── TransportService ───────────────────────────────────────────────────────

    @property
    def name(self) -> str:
        return "WebRTC P2P"

    @property
    def is_available(self) -> bool:
        return not self._closed

    @property
    def max_bandwidth_bps(self) -> int:
        return 100_000_000  # direct link — bounded by the local NIC

    @property
    def max_range_meters(self) -> int:
        return 0  # internet — unbounded

    @property
    def power_cost_relative(self) -> int:
        return 5  # dearer than local radio on the 1-10 scale

    @property
    def max_concurrent_peers(self) -> int:
        return 256

    @property
    def metrics(self) -> PerTransportMetrics:
        """Per-transport EWMA metrics (sample count, RTT, loss, throughput)."""
        return self._metrics

    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        if self._closed or not peer_uhid:
            return False

        link = await self._get_or_create_link(peer_uhid, as_initiator=True)
        if link is None:
            return False

        ok = await link.send(data, _CONNECT_TIMEOUT_SECONDS)
        self._metrics.record_sample(0, ok, len(data) if ok else 0)
        return ok

    async def send_stream_async(self, peer_uhid: str, data_stream: asyncio.StreamReader) -> bool:
        try:
            data = await data_stream.read()
        except Exception:
            return False
        return await self.send_async(peer_uhid, data)

    def is_connected(self, peer_uhid: str) -> bool:
        link = self._peers.get(peer_uhid)
        return link is not None and link.is_open

    def on_data_received(self, callback: Callable[[str, bytes], None]) -> None:
        self._on_data = callback

    async def close(self) -> None:
        """Tear down all peer connections."""
        async with self._lock:
            self._closed = True
            peers = list(self._peers.values())
            self._peers.clear()
        for link in peers:
            await link.close()

    # ── Signalling inbound ──────────────────────────────────────────────────────

    def _handle_signal(self, signal: Signal) -> None:
        # The signalling bus invokes this synchronously from its pump; the actual SDP/ICE
        # work is async, so hand it to the loop.
        if self._closed or signal.to_uhid != self._local_uhid:
            return
        asyncio.ensure_future(self._handle_signal_async(signal))

    async def _handle_signal_async(self, signal: Signal) -> None:
        try:
            if signal.type == SignalType.OFFER:
                link = await self._get_or_create_link(signal.from_uhid, as_initiator=False)
                if link is not None and signal.sdp is not None:
                    await link.accept_offer(signal.sdp)
            elif signal.type == SignalType.ANSWER:
                link = self._peers.get(signal.from_uhid)
                if link is not None and signal.sdp is not None:
                    await link.accept_answer(signal.sdp)
            elif signal.type == SignalType.CANDIDATE:
                link = self._peers.get(signal.from_uhid)
                if link is not None:
                    await link.add_remote_candidate(signal)
        except Exception:
            # A signalling failure must not crash the loop; ICE re-gathers on reconnect.
            pass

    async def _get_or_create_link(self, peer_uhid: str, as_initiator: bool) -> Optional["_PeerLink"]:
        async with self._lock:
            if self._closed:
                return None
            existing = self._peers.get(peer_uhid)
            if existing is not None and not existing.is_closed:
                link = existing
                created = False
            else:
                link = _PeerLink(
                    self._local_uhid,
                    peer_uhid,
                    self._ice_servers,
                    self._signaling,
                    self._on_data,
                )
                self._peers[peer_uhid] = link
                created = True

        if created:
            await link.start(as_initiator)

        # The initiator's open-wait is governed by send() (a single _CONNECT_TIMEOUT_SECONDS
        # gate); waiting here too would double the timeout for a peer that never answers.
        return link


class _PeerLink:
    """One WebRTC connection to a single peer.

    An :class:`RTCPeerConnection` plus its :class:`RTCDataChannel`, driving the
    offer/answer/ICE handshake over a :class:`Signaling` channel and surfacing received
    bytes through the transport's data callback.
    """

    def __init__(
        self,
        local_uhid: str,
        peer_uhid: str,
        ice_servers: List[RTCIceServer],
        signaling: Signaling,
        on_data: Optional[Callable[[str, bytes], None]],
    ) -> None:
        self._local_uhid = local_uhid
        self._peer_uhid = peer_uhid
        self._signaling = signaling
        self._on_data = on_data
        self._pc = RTCPeerConnection(RTCConfiguration(iceServers=list(ice_servers)))
        self._channel: Optional[RTCDataChannel] = None
        self._open_event = asyncio.Event()
        self._closed = False

        @self._pc.on("datachannel")
        def _on_datachannel(channel: RTCDataChannel) -> None:  # responder receives the channel
            self._attach(channel)

        @self._pc.on("icecandidate")
        def _on_icecandidate(candidate: Optional[RTCIceCandidate]) -> None:
            self._on_local_ice_candidate(candidate)

        @self._pc.on("connectionstatechange")
        async def _on_connection_state_change() -> None:
            if self._pc.connectionState in ("failed", "disconnected", "closed"):
                self._mark_closed()

    @property
    def is_open(self) -> bool:
        return self._channel is not None and self._channel.readyState == "open"

    @property
    def is_closed(self) -> bool:
        return self._closed

    async def start(self, as_initiator: bool) -> None:
        """Begin the handshake. The initiator creates the data channel and sends the offer."""
        if not as_initiator:
            return  # responder waits for the inbound offer (accept_offer)

        channel = self._pc.createDataChannel(_DATA_CHANNEL_LABEL)
        self._attach(channel)

        offer = await self._pc.createOffer()
        await self._pc.setLocalDescription(offer)
        await self._signaling.send_signal(
            self._peer_uhid,
            Signal(
                from_uhid=self._local_uhid,
                to_uhid=self._peer_uhid,
                type=SignalType.OFFER,
                sdp=self._pc.localDescription.sdp,
            ),
        )

    async def accept_offer(self, sdp: str) -> None:
        await self._pc.setRemoteDescription(RTCSessionDescription(sdp=sdp, type="offer"))
        answer = await self._pc.createAnswer()
        await self._pc.setLocalDescription(answer)
        await self._signaling.send_signal(
            self._peer_uhid,
            Signal(
                from_uhid=self._local_uhid,
                to_uhid=self._peer_uhid,
                type=SignalType.ANSWER,
                sdp=self._pc.localDescription.sdp,
            ),
        )

    async def accept_answer(self, sdp: str) -> None:
        await self._pc.setRemoteDescription(RTCSessionDescription(sdp=sdp, type="answer"))

    async def add_remote_candidate(self, signal: Signal) -> None:
        if not signal.candidate:
            return
        # aiortc takes a parsed RTCIceCandidate, not the raw SDP "candidate:" line, so
        # parse it and re-attach the mid / m-line index the signal carried.
        candidate = candidate_from_sdp(signal.candidate)
        candidate.sdpMid = signal.sdp_mid
        candidate.sdpMLineIndex = signal.sdp_mline_index
        await self._pc.addIceCandidate(candidate)

    def _on_local_ice_candidate(self, candidate: Optional[RTCIceCandidate]) -> None:
        if candidate is None:
            return  # nil candidate signals end-of-gathering
        asyncio.ensure_future(
            self._signaling.send_signal(
                self._peer_uhid,
                Signal(
                    from_uhid=self._local_uhid,
                    to_uhid=self._peer_uhid,
                    type=SignalType.CANDIDATE,
                    candidate=candidate_to_sdp(candidate),
                    sdp_mid=candidate.sdpMid,
                    sdp_mline_index=candidate.sdpMLineIndex or 0,
                ),
            )
        )

    def _attach(self, channel: RTCDataChannel) -> None:
        self._channel = channel

        @channel.on("open")
        def _on_open() -> None:
            self._open_event.set()

        @channel.on("close")
        def _on_close() -> None:
            self._mark_closed()

        @channel.on("message")
        def _on_message(message: object) -> None:
            if self._on_data is None:
                return
            # A data channel can carry text or binary; AetherNet frames are bytes.
            data = message.encode() if isinstance(message, str) else message
            self._on_data(self._peer_uhid, data)

        # If the channel was handed to us already open (responder fast path), unblock now.
        if channel.readyState == "open":
            self._open_event.set()

    def _mark_closed(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._open_event.set()  # unblock any waiter; is_open stays False

    async def wait_open(self, timeout_seconds: float) -> bool:
        if self.is_open:
            return True
        if self._closed:
            return False
        try:
            await asyncio.wait_for(self._open_event.wait(), timeout_seconds)
        except asyncio.TimeoutError:
            return False
        return self.is_open

    async def send(self, data: bytes, open_timeout_seconds: float) -> bool:
        if not await self.wait_open(open_timeout_seconds):
            return False
        channel = self._channel
        if channel is None:
            return False
        try:
            channel.send(data)
            # aiortc queues the send on the SCTP transport; yield so it flushes promptly.
            await asyncio.sleep(0)
            return True
        except Exception:
            return False

    async def close(self) -> None:
        self._mark_closed()
        if self._channel is not None:
            try:
                self._channel.close()
            except Exception:
                pass  # best effort
        try:
            await self._pc.close()
        except Exception:
            pass  # best effort
