# SPDX-License-Identifier: MIT

"""Native circuit-relay-v2 over the Aether mesh.

Decentralised any-node relaying: a node that cannot reach a peer directly routes
through a third node reachable to both. The wire frame is byte-identical across
every language SDK and pinned by ``fixtures/circuit-relay/``.
"""

from aethernet.circuitrelay.frame import (
    RELAY_FRAME_VERSION,
    MessageType,
    RelayFrame,
    Status,
    deserialize,
    serialize,
)
from aethernet.circuitrelay.transport import (
    CircuitRelayOptions,
    RelayLink,
    Transport,
)

__all__ = [
    "RELAY_FRAME_VERSION",
    "MessageType",
    "Status",
    "RelayFrame",
    "serialize",
    "deserialize",
    "CircuitRelayOptions",
    "RelayLink",
    "Transport",
]
