# SPDX-License-Identifier: MIT

"""DtnBundleReceivedEvent — payload delivered to DtnService.on_bundle_received
the moment a DTN bundle addressed to the local node lands. Added in v1.2.0
to close the Wave-16 gap surfaced by Issue #59 (no inbound-bundle event).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from uuid import UUID

from aethernet.models import BundlePriority


@dataclass
class DtnBundleReceivedEvent:
    """Event payload delivered to DtnService.on_bundle_received the moment a
    DTN bundle arrives whose final recipient is the local node — i.e., a
    bundle addressed TO us has just been delivered locally by a peer or by
    the receive pump itself.

    Distinct from DtnDeliveryReceipt (delivered via on_bundle_delivered),
    which fires on the original sender side once a delivery confirmation
    flows back. Consumers that want to know "did a bundle arrive for me?"
    should set on_bundle_received; consumers that want to know "did my
    outbound bundle reach the recipient?" should set on_bundle_delivered.
    """

    bundle_id: UUID
    sender_uhid: str
    recipient_uhid: str
    encrypted_payload: bytes
    priority: BundlePriority
    hop_count: int
    received_at_utc: datetime = field(default_factory=datetime.utcnow)
