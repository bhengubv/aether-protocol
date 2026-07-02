# SPDX-License-Identifier: MIT
"""Presence — privacy-preserving "I'm here" beacons + "who's around?" queries.

PacketType.PresenceBeacon (21) + PacketType.PresenceQuery (22).
"""

from .service import (
    PresenceBeaconPayload,
    PresenceQueryPayload,
    PresenceService,
    encode_beacon_payload,
    encode_query_payload,
)

__all__ = [
    "PresenceBeaconPayload",
    "PresenceQueryPayload",
    "PresenceService",
    "encode_beacon_payload",
    "encode_query_payload",
]
