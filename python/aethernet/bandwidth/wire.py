# SPDX-License-Identifier: MIT

"""WIRE bindings for the three ABMF PacketTypes — BandwidthProbe(53),
BandwidthAck(54), BandwidthGossip(55).

Python port of ``AetherNet.Core/Bandwidth/BandwidthWireService.cs``, byte-identical
to the C# reference and every other language SDK (fixtures/bandwidth/vectors.json).

Wire format
-----------
All multi-byte integers are **little-endian**, matching the packet-serializer
convention. NO version byte — the layouts are exactly the ones documented on the
``PacketType`` members. Byte-identity gate: fixtures/bandwidth/vectors.json (hex).

* **Probe(53)**  : ``sequence u32 | sender_send_us i64``                    (``"<Iq"``, 12 B)
* **Ack(54)**    : ``sequence u32 | sender_send_us i64 | receiver_receive_us i64 |
  receiver_send_us i64 | probe_bytes i32``                                  (``"<Iqqqi"``, 32 B)
* **Gossip(55)** : ``btlbw_bps i64 | rtprop_us i32 | confidence u8``        (``"<qiB"``, 13 B)

``sender_receive_us`` is filled locally by the prober on receipt — it is NOT on the
wire (the serializer omits it; the deserializer sets it to 0). A gossip's
``peer_uhid`` / ``transport_name`` / ``measured_at`` are not carried in the body;
the service fills ``peer_uhid`` from the enclosing packet's source.

Reuses the existing ABMF types from :mod:`aethernet.bandwidth.models`
(:class:`BandwidthProbeAck`, :class:`BandwidthGossipPayload`,
:class:`BandwidthConfidence`) and adds a small :class:`BandwidthProbe` dataclass.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, replace
from typing import Callable, Optional

from aethernet.bandwidth.models import (
    BandwidthConfidence,
    BandwidthGossipPayload,
    BandwidthProbeAck,
)
from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


# ── Probe wire model ──────────────────────────────────────────────────────────


@dataclass(frozen=True)
class BandwidthProbe:
    """A latency/throughput probe request (PacketType.BandwidthProbe = 53 body).

    ``sender_send_us`` is microseconds since Unix epoch on the sender's local clock.
    """

    sequence: int
    sender_send_us: int


@dataclass(frozen=True)
class BandwidthProbeReceived:
    """An inbound probe plus the peer that sent it (so the host can reply with an ack)."""

    probe: BandwidthProbe
    from_uhid: str


# ── Binary codec ──────────────────────────────────────────────────────────────

_MAX_I32 = 2_147_483_647  # int.MaxValue — RtPropUs is clamped into [0, this] like the C# codec.


def serialize_probe(p: BandwidthProbe) -> bytes:
    """Serialize a Probe(53) body: ``sequence u32 | sender_send_us i64`` (12 B, LE)."""
    return struct.pack("<Iq", p.sequence & 0xFFFFFFFF, p.sender_send_us)


def deserialize_probe(b: bytes) -> BandwidthProbe:
    """Decode a Probe(53) body. Raises ``ValueError`` if shorter than 12 bytes."""
    if len(b) < 12:
        raise ValueError("BandwidthProbe payload too short")
    sequence, sender_send_us = struct.unpack_from("<Iq", b, 0)
    return BandwidthProbe(sequence=sequence, sender_send_us=sender_send_us)


def serialize_ack(a: BandwidthProbeAck) -> bytes:
    """Serialize an Ack(54) body (32 B, LE). ``sender_receive_us`` is local-only and omitted."""
    return struct.pack(
        "<Iqqqi",
        a.sequence & 0xFFFFFFFF,
        a.sender_send_us,
        a.receiver_receive_us,
        a.receiver_send_us,
        a.probe_bytes,
    )


def deserialize_ack(b: bytes) -> BandwidthProbeAck:
    """Decode an Ack(54) body. ``sender_receive_us`` is not on the wire → 0.

    Raises ``ValueError`` if shorter than 32 bytes.
    """
    if len(b) < 32:
        raise ValueError("BandwidthProbeAck payload too short")
    sequence, sender_send_us, receiver_receive_us, receiver_send_us, probe_bytes = struct.unpack_from(
        "<Iqqqi", b, 0
    )
    return BandwidthProbeAck(
        sequence=sequence,
        sender_send_us=sender_send_us,
        receiver_receive_us=receiver_receive_us,
        receiver_send_us=receiver_send_us,
        sender_receive_us=0,  # filled by the prober on receipt, not carried on the wire
        probe_bytes=probe_bytes,
    )


def serialize_gossip(g: BandwidthGossipPayload) -> bytes:
    """Serialize a Gossip(55) body: ``btlbw_bps i64 | rtprop_us i32 | confidence u8`` (13 B, LE).

    ``rt_prop_us`` is clamped into ``[0, int.MaxValue]`` to match the C# codec.
    """
    rt_prop_us = max(0, min(_MAX_I32, g.rt_prop_us))
    return struct.pack("<qiB", g.btlbw_bps, rt_prop_us, g.confidence.value & 0xFF)


def deserialize_gossip(b: bytes) -> BandwidthGossipPayload:
    """Decode a Gossip(55) body. ``peer_uhid`` / ``transport_name`` default empty; the
    service fills ``peer_uhid`` from the packet. Raises ``ValueError`` if shorter than 13 bytes.
    """
    if len(b) < 13:
        raise ValueError("BandwidthGossipPayload payload too short")
    btlbw_bps, rt_prop_us = struct.unpack_from("<qi", b, 0)
    confidence = BandwidthConfidence(b[12])
    return BandwidthGossipPayload(
        peer_uhid="",
        transport_name="",
        btlbw_bps=btlbw_bps,
        rt_prop_us=rt_prop_us,
        confidence=confidence,
        measured_at=0.0,
    )


# ── Wire service ──────────────────────────────────────────────────────────────


class BandwidthWireService:
    """Binds the three ABMF PacketTypes to the mesh: send probes (directed) + their acks
    (directed reply), and broadcast/receive warm-start gossip.

    Inbound packets surface via the ``on_probe_received`` / ``on_ack_received`` /
    ``on_gossip_received`` callbacks; the host feeds them into the estimator
    (record probe result / warm from gossip) and replies to probes.

    Mirrors the C# ``AetherNet.Bandwidth.BandwidthWireService``.
    """

    def __init__(self, sender, logger=None) -> None:
        if sender is None:
            raise ValueError("sender must not be None")
        self._sender = sender
        self._logger = logger
        #: Fired with a :class:`BandwidthProbeReceived` when an inbound probe arrives.
        self.on_probe_received: Optional[Callable[[BandwidthProbeReceived], None]] = None
        #: Fired with a :class:`BandwidthProbeAck` when an inbound ack arrives.
        self.on_ack_received: Optional[Callable[[BandwidthProbeAck], None]] = None
        #: Fired with a :class:`BandwidthGossipPayload` (peer_uhid set from the packet source).
        self.on_gossip_received: Optional[Callable[[BandwidthGossipPayload], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def send_probe(self, peer_uhid: str, probe: BandwidthProbe) -> bool:
        """Send a directed BandwidthProbe(53) to a peer. Returns True if delivered."""
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")
        if probe is None:
            raise ValueError("probe must not be None")
        return await self._send_directed(
            peer_uhid, PacketType.BandwidthProbe, serialize_probe(probe)
        )

    async def send_ack(self, peer_uhid: str, ack: BandwidthProbeAck) -> bool:
        """Send a directed BandwidthAck(54) reply to the prober. Returns True if delivered."""
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")
        if ack is None:
            raise ValueError("ack must not be None")
        return await self._send_directed(
            peer_uhid, PacketType.BandwidthAck, serialize_ack(ack)
        )

    async def _send_directed(self, peer_uhid: str, ptype: PacketType, payload: bytes) -> bool:
        packet = MeshPacket(
            type=ptype,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=DEFAULT_TTL,
            payload=payload,
        )
        return await self._sender.send(packet, peer_uhid)

    async def broadcast_gossip(self, gossip: BandwidthGossipPayload) -> int:
        """Broadcast a BandwidthGossip(55) warm-start estimate. Returns the number of peers reached."""
        if gossip is None:
            raise ValueError("gossip must not be None")
        packet = MeshPacket(
            type=PacketType.BandwidthGossip,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=serialize_gossip(gossip),
        )
        return await self._sender.broadcast(packet)

    async def handle(self, packet: MeshPacket) -> bool:
        """Dispatch an inbound bandwidth packet to the matching callback.

        Returns ``False`` on a wrong packet type or a malformed/short body; ``True`` otherwise.
        """
        if packet is None:
            return False
        try:
            if packet.type == PacketType.BandwidthProbe:
                probe = deserialize_probe(packet.payload)
                if self.on_probe_received is not None:
                    self.on_probe_received(
                        BandwidthProbeReceived(probe=probe, from_uhid=packet.source_uhid)
                    )
                return True

            if packet.type == PacketType.BandwidthAck:
                ack = deserialize_ack(packet.payload)
                if self.on_ack_received is not None:
                    self.on_ack_received(ack)
                return True

            if packet.type == PacketType.BandwidthGossip:
                gossip = replace(
                    deserialize_gossip(packet.payload), peer_uhid=packet.source_uhid
                )
                if self.on_gossip_received is not None:
                    self.on_gossip_received(gossip)
                return True

            return False
        except (ValueError, struct.error) as exc:
            self._log(
                f"Bandwidth {packet.type.name} from {packet.source_uhid}: "
                f"malformed payload — dropped: {exc}"
            )
            return False
