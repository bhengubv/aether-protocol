# SPDX-License-Identifier: MIT

"""Aether identity primitives."""

from aethernet.identity.aethernet_tag import AetherNetTag
from aethernet.identity import ephemeral_routing_id
from aethernet.identity import erid_announcement_codec
from aethernet.identity import peer_id
from aethernet.identity.ephemeral_routing_id import (
    DEFAULT_EPOCH_SECONDS,
    DEFAULT_LENGTH,
    derive,
    derive_for_epoch,
    derive_routing_key,
    epoch_for,
)
from aethernet.identity.erid_directory import EridDirectory
from aethernet.identity.peer_id import (
    ED25519_PUBLIC_KEY_LENGTH,
    from_ed25519_public_key,
)

__all__ = [
    "AetherNetTag",
    "ephemeral_routing_id",
    "erid_announcement_codec",
    "peer_id",
    "EridDirectory",
    "DEFAULT_EPOCH_SECONDS",
    "DEFAULT_LENGTH",
    "derive",
    "derive_for_epoch",
    "derive_routing_key",
    "epoch_for",
    "ED25519_PUBLIC_KEY_LENGTH",
    "from_ed25519_public_key",
]
