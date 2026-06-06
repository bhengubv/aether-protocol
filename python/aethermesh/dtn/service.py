# SPDX-License-Identifier: MIT

"""Default DTN service. Bundles are JSON-encoded into MeshPacket(DtnBundle) payloads."""

from __future__ import annotations

import json
import logging
from datetime import datetime, timedelta
from typing import Callable, Optional
from uuid import UUID, uuid4

from aethermesh import constants
from aethermesh.extensibility import (
    BackendClient,
    IncentiveProvider,
    NoopBackendClient,
    NoopIncentiveProvider,
)
from aethermesh.models import (
    BundlePriority,
    BundleStatus,
    CustodyRecord,
    DtnBundle,
    DtnDeliveryReceipt,
)
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.reputation import NodeReputationService
from aethermesh.routing.sender import MeshSender
from aethermesh.dtn.store import BundleStore, InMemoryBundleStore
from aethermesh.dtn.strategy import GeohashEpidemicStrategy, ReplicationStrategy


_LOG = logging.getLogger(__name__)


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
            payload=_encode_bundle(bundle),
        )

    async def _handle_bundle(self, packet: MeshPacket) -> None:
        bundle = _decode_bundle(packet.payload)
        if bundle is None:
            return

        if bundle.recipient_uhid == self._sender.local_uhid:
            bundle.status = BundleStatus.DELIVERED
            await self._store.save(bundle)
            await self._send_delivery_receipt(bundle)
            if self._reputation is not None:
                self._reputation.record_delivery_success(packet.source_uhid, 0)
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
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        bundle_id = _try_uuid(data.get("bundle_id"))
        accepted = bool(data.get("accepted"))
        if bundle_id is None:
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
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        bundle_id = _try_uuid(data.get("bundle_id"))
        if bundle_id is None:
            return
        receipt = DtnDeliveryReceipt(
            bundle_id=bundle_id,
            recipient_uhid=str(data.get("recipient_uhid", "")),
            total_hops=int(data.get("total_hops", 0)),
            total_custody_transfers=int(data.get("total_custody_transfers", 0)),
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
        body = json.dumps({"bundle_id": str(bundle_id), "accepted": accepted}).encode("utf-8")
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
        body = json.dumps(
            {
                "bundle_id": str(bundle.id),
                "recipient_uhid": bundle.recipient_uhid,
                "total_hops": bundle.hop_count,
                "total_custody_transfers": len(custody),
                "delivered_at_ms": int(datetime.utcnow().timestamp() * 1000),
            }
        ).encode("utf-8")
        packet = MeshPacket(
            type=PacketType.DtnDeliveryReceipt,
            source_uhid=self._sender.local_uhid,
            destination_uhid=bundle.sender_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )
        await self._sender.send(packet, bundle.sender_uhid)


def _encode_bundle(bundle: DtnBundle) -> bytes:
    payload = {
        "id": str(bundle.id),
        "sender_uhid": bundle.sender_uhid,
        "recipient_uhid": bundle.recipient_uhid,
        "encrypted_payload": list(bundle.encrypted_payload),
        "priority": int(bundle.priority),
        "status": int(bundle.status),
        "copy_count": bundle.copy_count,
        "max_copies": bundle.max_copies,
        "sender_geohash": bundle.sender_geohash,
        "recipient_last_geohash": bundle.recipient_last_geohash,
        "hop_count": bundle.hop_count,
        "created_at_ms": int(bundle.created_at.timestamp() * 1000),
        "expires_at_ms": int(bundle.expires_at.timestamp() * 1000),
    }
    return json.dumps(payload).encode("utf-8")


def _decode_bundle(payload: bytes) -> Optional[DtnBundle]:
    try:
        data = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    bundle_id = _try_uuid(data.get("id")) or uuid4()
    return DtnBundle(
        id=bundle_id,
        sender_uhid=str(data.get("sender_uhid", "")),
        recipient_uhid=str(data.get("recipient_uhid", "")),
        encrypted_payload=bytes(data.get("encrypted_payload", [])),
        priority=BundlePriority(int(data.get("priority", BundlePriority.NORMAL))),
        status=BundleStatus(int(data.get("status", BundleStatus.PENDING))),
        copy_count=int(data.get("copy_count", 1)),
        max_copies=int(data.get("max_copies", constants.DTN_MAX_COPIES)),
        sender_geohash=data.get("sender_geohash"),
        recipient_last_geohash=data.get("recipient_last_geohash"),
        hop_count=int(data.get("hop_count", 0)),
        created_at=datetime.utcfromtimestamp(int(data.get("created_at_ms", 0)) / 1000),
        expires_at=datetime.utcfromtimestamp(int(data.get("expires_at_ms", 0)) / 1000),
    )


def _try_uuid(value: object) -> Optional[UUID]:
    if isinstance(value, UUID):
        return value
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            return None
    return None
