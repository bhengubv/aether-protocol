# SPDX-License-Identifier: MIT

"""Multi-transport manager that routes a send through the best available transport.

Mirrors the C# ``AetherNet.Transport.Services.TransportManager``. Selection order:

1. NearLink   — lowest power, highest range (typed slot)
2. BLE        — small payloads (<= 1 KB), low power (typed slot)
3. Wi-Fi Direct — large payloads, highest bandwidth (typed slot)
4. CircleLink — extensible custom transport (typed slot)
5. BLE fallback for large payloads
6. Additional transports — sorted ascending by ``power_cost_relative``

Falls through all available transports until one succeeds or all fail. The
circuit-relay-v2 transport (``power_cost_relative`` 90) registers as an *additional*
transport, so it is auto-selected LAST — the serverless, any-node fallback used only
when no cheaper/direct transport delivered.

Typed slots are optional; the minimal real deployment (and the gap-2 acceptance
test) supplies only ``additional_transports``. Any object satisfying the
:class:`~aethernet.transport.transport_service.TransportService` surface
(duck-typed: ``name``, ``is_available``, ``power_cost_relative``, ``send_async``,
``send_stream_async``, ``on_data_received``) is accepted.
"""

from __future__ import annotations

import asyncio
from typing import Callable, List, Optional, Sequence

from aethernet.transport.transport_service import TransportService

_BLE_PAYLOAD_THRESHOLD = 1024  # 1 KB

#: ``(sender_uhid, data, via_transport_name)`` — the manager's received-data contract.
DataReceivedCallback = Callable[[str, bytes, str], None]


class TransportManager:
    """Routes packets through the best available transport, falling through on failure.

    A faithful port of the C# ``TransportManager``: NearLink -> BLE(small) ->
    Wi-Fi Direct -> CircleLink -> BLE(large) -> additional transports (ascending
    power cost). Received data surfaces through :meth:`on_data_received` tagged with
    the delivering transport's :attr:`~TransportService.name`.
    """

    def __init__(
        self,
        ble: Optional[TransportService] = None,
        circle_link: Optional[TransportService] = None,
        wifi_direct: Optional[TransportService] = None,
        near_link: Optional[TransportService] = None,
        additional_transports: Optional[Sequence[TransportService]] = None,
    ) -> None:
        self._ble = ble
        self._circle_link = circle_link
        self._wifi_direct = wifi_direct
        self._near_link = near_link

        # Filter out transports already handled by typed slots to avoid double-routing,
        # then order additional transports by ascending power cost (relay -> last).
        known = {id(t) for t in (ble, circle_link, wifi_direct, near_link) if t is not None}
        self._additional: List[TransportService] = sorted(
            (t for t in (additional_transports or []) if id(t) not in known),
            key=lambda t: t.power_cost_relative,
        )

        self._on_data: Optional[DataReceivedCallback] = None

        # Metrics.
        self._additional_send_count = 0
        self._additional_bytes_sent = 0
        self._total_failures = 0

        self._subscribe_to_data_events()

    # ── Received-data contract ──────────────────────────────────────────────────

    def on_data_received(self, callback: Optional[DataReceivedCallback]) -> None:
        """Register the callback invoked when data arrives from any managed transport.
        The callback receives ``(sender_uhid, data, via_transport_name)``."""
        self._on_data = callback

    # ── Send ────────────────────────────────────────────────────────────────────

    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        """Send ``data`` to ``peer_uhid`` via the first transport that succeeds,
        trying them in the C# selection order. Returns ``True`` on first success."""
        data_length = len(data)

        # 1. NearLink — always preferred when available.
        if self._is_available(self._near_link):
            if await self._near_link.send_async(peer_uhid, data):
                return True

        # 2. BLE — preferred for small payloads (<= 1 KB).
        if self._is_available(self._ble) and data_length <= _BLE_PAYLOAD_THRESHOLD:
            if await self._ble.send_async(peer_uhid, data):
                return True

        # 3. Wi-Fi Direct — preferred for larger payloads.
        if self._is_available(self._wifi_direct):
            if await self._wifi_direct.send_async(peer_uhid, data):
                return True

        # 4. CircleLink.
        if self._is_available(self._circle_link):
            if await self._circle_link.send_async(peer_uhid, data):
                return True

        # 5. BLE fallback for large payloads (if NearLink and Wi-Fi Direct both failed).
        if self._is_available(self._ble) and data_length > _BLE_PAYLOAD_THRESHOLD:
            if await self._ble.send_async(peer_uhid, data):
                return True

        # 6. Additional transports (ascending power cost) — the circuit relay lands here.
        for transport in self._additional:
            if not transport.is_available:
                continue
            if await transport.send_async(peer_uhid, data):
                self._additional_send_count += 1
                self._additional_bytes_sent += data_length
                return True

        self._total_failures += 1
        return False

    async def send_stream_async(
        self, peer_uhid: str, data_stream: asyncio.StreamReader
    ) -> bool:
        """Send a stream via the first transport that succeeds. The stream is read
        once, so failed attempts cannot rewind — additional transports are tried on a
        best-effort basis in selection order."""
        # NearLink -> Wi-Fi Direct -> CircleLink -> BLE -> additional (mirrors C#).
        for transport in self._stream_order():
            if not self._is_available(transport):
                continue
            if await transport.send_stream_async(peer_uhid, data_stream):
                return True
        self._total_failures += 1
        return False

    # ── Diagnostics ─────────────────────────────────────────────────────────────

    @property
    def additional_send_count(self) -> int:
        """Number of sends delivered by an additional transport (e.g. the relay)."""
        return self._additional_send_count

    @property
    def total_failures(self) -> int:
        """Number of sends for which no transport succeeded."""
        return self._total_failures

    def dispose(self) -> None:
        """Detach the received-data callback."""
        self._on_data = None

    # ── Internal ────────────────────────────────────────────────────────────────

    @staticmethod
    def _is_available(transport: Optional[TransportService]) -> bool:
        return transport is not None and transport.is_available

    def _stream_order(self) -> List[TransportService]:
        order: List[TransportService] = []
        for t in (self._near_link, self._wifi_direct, self._circle_link, self._ble):
            if t is not None:
                order.append(t)
        order.extend(self._additional)
        return order

    def _subscribe_to_data_events(self) -> None:
        def _tag(via: str) -> Callable[[str, bytes], None]:
            def _forward(sender: str, data: bytes) -> None:
                cb = self._on_data
                if cb is not None:
                    cb(sender, data, via)

            return _forward

        if self._ble is not None:
            self._ble.on_data_received(_tag(self._ble.name))
        if self._wifi_direct is not None:
            self._wifi_direct.on_data_received(_tag(self._wifi_direct.name))
        if self._near_link is not None:
            self._near_link.on_data_received(_tag(self._near_link.name))
        if self._circle_link is not None:
            self._circle_link.on_data_received(_tag(self._circle_link.name))
        for transport in self._additional:
            transport.on_data_received(_tag(transport.name))
