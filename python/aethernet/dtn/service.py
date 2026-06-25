# SPDX-License-Identifier: MIT

"""Default DTN service. Bundle, custody-ack and delivery-receipt bodies are
encoded into MeshPacket payloads with the binary DtnEnvelope serializer
(see aethernet.dtn.envelope) for byte-identical cross-language interoperability."""

from __future__ import annotations

import logging
import struct
from datetime import datetime, timedelta
from typing import Callable, Optional
from uuid import UUID

from aethernet import constants
from aethernet.extensibility import (
    BackendClient,
    IncentiveProvider,
    NoopBackendClient,
    NoopIncentiveProvider,
)
from aethernet.models import (
    BundlePriority,
    BundleStatus,
    CustodyRecord,
    DtnBundle,
    DtnDeliveryReceipt,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.reputation import NodeReputationService
from aethernet.routing.sender import MeshSender
from aethernet.dtn.store import BundleStore, InMemoryBundleStore
from aethernet.dtn.strategy import GeohashEpidemicStrategy, ReplicationStrategy
from aethernet.dtn.bundle_received_event import DtnBundleReceivedEvent
from aethernet.dtn.envelope import (
    deserialize_bundle,
    deserialize_custody_ack,
    deserialize_delivery_receipt,
    serialize_bundle,
    serialize_custody_ack,
    serialize_delivery_receipt,
)


_LOG = logging.getLogger(__name__)

# Decode errors raised by the binary envelope reader on malformed input.
_DECODE_ERRORS = (ValueError, struct.error, IndexError)


class DtnService:
    """Default DTN service implementation. Three-tier delivery:
    direct mesh send → DTN epidemic replication → backend relay.
    """

    def __init__(
        self,
        sender: MeshSender,
        store: Optional[BundleStore] = None,
        strategy: Optional[ReplicationStrategy] = None,
        incentives: Optional[IncentiveProvider] = None,
        backend: Optional[BackendClient] = None,
    ) -> None:
        self._sender = sender
        self._store = store or InMemoryBundleStore()
        self._strategy = strategy or GeohashEpidemicStrategy()
        self._incentives = incentives or NoopIncentiveProvider()
        self._backend = backend or NoopBackendClient()
        self._reputation: Optional[NodeReputationService] = None
        self.on_bundle_delivered: Optional[Callable[[DtnDeliveryReceipt], None]] = None
        self.on_bundle_received: Optional[Callable[[DtnBundleReceivedEvent], None]] = None

    def set_reputation(self, reputation: Optional[NodeReputationService]) -> None:
        """Attach a :class:`NodeReputationService` to receive DTN reputation signals.

        Pass ``None`` to detach the reputation service.
        """
        self._reputation = reputation

    async def create_bundle(
        self,
        recipient_uhid: str,
        encrypted_payload: bytes,
        priority: BundlePriority = BundlePriority.NORMAL,
        recipient_last_geohash: Optional[str] = None,
    ) -> DtnBundle:
        if not recipient_uhid:
            raise ValueError("recipient_uhid must not be empty")

        bundle = DtnBundle(
            sender_uhid=self._sender.local_uhid,
            recipient_uhid=recipient_uhid,
            encrypted_payload=encrypted_payload,
            priority=priority,
            sender_geohash=self._sender.local_geohash,
            recipient_last_geohash=recipient_last_geohash,
            expires_at=datetime.utcnow() + timedelta(hours=constants.DTN_BUNDLE_TTL_HOURS),
        )
        await self._store.save(bundle)

        if await self._try_direct_delivery(bundle):
            bundle.status = BundleStatus.DELIVERED
            await self._store.save(bundle)
        return bundle

    async def handle(self, packet: MeshPacket) -> None:
        if packet.type == PacketType.DtnBundle:
            await self._handle_bundle(packet)
        elif packet.type == PacketType.DtnCustodyAck:
            await self._handle_custody_ack(packet)
        elif packet.type == PacketType.DtnDeliveryReceipt:
            await self._handle_delivery_receipt(packet)

    async def run_delivery_scan(self) -> None:
        active = await self._store.get_active()
        if not active:
            return
        peers = self._sender.get_connected_peers()
        local_geohash = self._sender.local_geohash

        for bundle in active:
            if bundle.status == BundleStatus.DELIVERED or bundle.is_expired:
                continue
            if await self._try_direct_delivery(bundle):
                bundle.status = BundleStatus.DELIVERED
                await self._store.save(bundle)
                continue
            if not peers or bundle.copy_count >= bundle.max_copies:
                continue
            for target in self._strategy.select_targets(bundle, peers, local_geohash):
                if bundle.copy_count >= bundle.max_copies:
                    break
                packet = self._build_bundle_packet(bundle, target)
                if await self._sender.send(packet, target):
                    bundle.copy_count += 1
                    await self._store.save(bundle)
                    await self._incentives.record_relay(self._sender.local_uhid, packet)

    async def expire_stale(self) -> int:
        return await self._store.expire_stale()

    async def get_active_bundles(self) -> list[DtnBundle]:
        return await self._store.get_active()

    async def _try_direct_delivery(self, bundle: DtnBundle) -> bool:
        packet = self._build_bundle_packet(bundle, bundle.recipient_uhid)
        for peer in self._sender.get_connected_peers():
            if peer.uhid == bundle.recipient_uhid:
                if await self._sender.send(packet, bundle.recipient_uhid):
                    return True
                break
        return await self._backend.sync_dtn_bundle(bundle)

    def _build_bundle_packet(self, bundle: DtnBundle, next_hop_uhid: str) -> MeshPacket:
        return MeshPacket(
            id=bundle.id,
            type=PacketType.DtnBundle,
            source_uhid=self._sender.local_uhid,
            destination_uhid=bundle.recipient_uhid,
            ttl=30,
            priority=min(255, max(0, int(bundle.priority))),
            payload=serialize_bundle(bundle),
        )

    async def _handle_bundle(self, packet: MeshPacket) -> None:
        try:
            bundle = deserialize_bundle(packet.payload)
        except _DECODE_ERRORS:
            return

        if bundle.recipient_uhid == self._sender.local_uhid:
            bundle.status = BundleStatus.DELIVERED
            await self._store.save(bundle)
            if self._reputation is not None:
                self._reputation.record_delivery_success(packet.source_uhid, 0)
            if self.on_bundle_received is not None:
                self.on_bundle_received(DtnBundleReceivedEvent(
                    bundle_id=bundle.id,
                    sender_uhid=bundle.sender_uhid,
                    recipient_uhid=bundle.recipient_uhid,
                    encrypted_payload=bundle.encrypted_payload,
                    priority=bundle.priority,
                    hop_count=bundle.hop_count,
                ))
            await self._send_delivery_receipt(bundle)
            return

        if await self._store.get_active_count() >= constants.DTN_MAX_BUNDLES_PER_NODE:
            await self._send_custody_ack(bundle.id, packet.source_uhid, accepted=False)
            return

        bundle.status = BundleStatus.IN_CUSTODY
        bundle.hop_count += 1
        await self._store.save(bundle)
        await self._store.save_custody(
            CustodyRecord(
                bundle_id=bundle.id,
                from_uhid=packet.source_uhid,
                to_uhid=self._sender.local_uhid,
                accepted=True,
            )
        )
        await self._send_custody_ack(bundle.id, packet.source_uhid, accepted=True)
        await self._incentives.record_relay(self._sender.local_uhid, packet)

    async def _handle_custody_ack(self, packet: MeshPacket) -> None:
        try:
            bundle_id, accepted = deserialize_custody_ack(packet.payload)
        except _DECODE_ERRORS:
            return
        if not accepted:
            if self._reputation is not None:
                self._reputation.record_custody_refusal(packet.source_uhid)
            return
        bundle = await self._store.get(bundle_id)
        if bundle is None:
            return
        bundle.copy_count += 1
        await self._store.save(bundle)

    async def _handle_delivery_receipt(self, packet: MeshPacket) -> None:
        try:
            bundle_id, recipient_uhid, total_hops, total_custody_transfers, _delivered_at_ms = (
                deserialize_delivery_receipt(packet.payload)
            )
        except _DECODE_ERRORS:
            return
        receipt = DtnDeliveryReceipt(
            bundle_id=bundle_id,
            recipient_uhid=recipient_uhid,
            total_hops=total_hops,
            total_custody_transfers=total_custody_transfers,
        )
        bundle = await self._store.get(bundle_id)
        if bundle is not None:
            bundle.status = BundleStatus.DELIVERED
            await self._store.save(bundle)
        if self.on_bundle_delivered:
            self.on_bundle_delivered(receipt)

    async def _send_custody_ack(self, bundle_id: UUID, to_uhid: str, accepted: bool) -> None:
        if not to_uhid:
            return
        body = serialize_custody_ack(bundle_id, accepted)
        packet = MeshPacket(
            type=PacketType.DtnCustodyAck,
            source_uhid=self._sender.local_uhid,
            destination_uhid=to_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )
        await self._sender.send(packet, to_uhid)

    async def _send_delivery_receipt(self, bundle: DtnBundle) -> None:
        if not bundle.sender_uhid or bundle.sender_uhid == self._sender.local_uhid:
            return
        custody = await self._store.get_custody_records(bundle.id)
        body = serialize_delivery_receipt(
            bundle.id,
            bundle.recipient_uhid,
            bundle.hop_count,
            len(custody),
            int(datetime.utcnow().timestamp() * 1000),
        )
        packet = MeshPacket(
            type=PacketType.DtnDeliveryReceipt,
            source_uhid=self._sender.local_uhid,
            destination_uhid=bundle.sender_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )
        await self._sender.send(packet, bundle.sender_uhid)
