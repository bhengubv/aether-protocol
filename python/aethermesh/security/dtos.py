# SPDX-License-Identifier: MIT

"""Persistence DTOs for the Signal-protocol session and pre-key state.

These are JSON-serialisable snapshots of the live in-memory state held by
:class:`aethermesh.security.signal_protocol.SignalProtocolService`. They are
written through :class:`aethermesh.security.session_store.SignalSessionStore`
and :class:`aethermesh.security.pre_key_store.PreKeyStore` implementations and
read back on process restart so that:

* Sessions survive — the Double-Ratchet state for every active peer is
  persisted on every encrypt/decrypt mutation, so a fresh service against
  the same store comes up with every previously-active session ready.
* Long-term identity keys survive — the Ed25519 + X25519 keypairs are
  written on first construction and reloaded on every subsequent one, so
  bundles published to peers don't get invalidated by a process restart.
* The signed-pre-key history survives — including the ages needed to
  decide whether to rotate on the next bundle generation.
* The OPK pool survives — each consumed OPK is removed from persistent
  storage so a replay attempt after a restart is rejected.

JSON shapes mirror the C# DTOs (``StoredIdentityKeys``,
``StoredSignedPreKey``, ``StoredOneTimePreKey``, ``SignalSessionDto``)
field-for-field — bytes go over the wire as base64 strings.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional


@dataclass
class StoredIdentityKeys:
    """Long-term identity key material that survives across process restarts.

    The Ed25519 keypair signs pre-key bundles; the X25519 keypair
    participates in X3DH agreement. Both private halves stay on the node
    and are never transmitted.

    ``local_uhid`` is persisted alongside the keys so that ``encrypt``
    still works after a restart without the host having to call
    ``set_local_uhid`` again.
    """

    ed25519_private_key: bytes
    ed25519_public_key: bytes
    x25519_private_key: bytes
    x25519_public_key: bytes
    local_uhid: Optional[str] = None


@dataclass
class StoredSignedPreKey:
    """One signed pre-key entry as stored in the SPK history.

    Each rotation generates a new entry; the active entry is the
    most-recently-generated one. Older entries are retained for the
    configured rotation window so that messages signed under a
    recently-rotated SPK can still complete X3DH on the responder side.

    ``generated_at_unix_ms`` is the millisecond Unix timestamp when this
    SPK was generated — used to compute age against the rotation interval.
    Stored as an integer (rather than an ISO datetime) so the on-disk
    format is byte-identical to the C# reference.
    """

    id: int
    private_key: bytes
    public_key: bytes
    signature: bytes
    generated_at_unix_ms: int


@dataclass
class StoredSignedPreKeyHistory:
    """Full signed-pre-key history: oldest first, newest last.

    The newest entry is the active SPK that gets handed out in bundles.
    Older entries are retained for the rotation window. Empty until the
    first ``generate_pre_key_bundle`` call.
    """

    entries: List[StoredSignedPreKey] = field(default_factory=list)


@dataclass
class StoredOneTimePreKey:
    """One one-time pre-key in the pool.

    Removed from the store on consumption (Signal §3.3 — each OPK is
    consumed exactly once). ``issued`` is True iff this OPK has been
    advertised in at least one bundle but not yet consumed; un-issued
    OPKs sit in the available queue waiting for the next bundle.
    """

    id: int
    private_key: bytes
    public_key: bytes
    issued: bool


@dataclass
class StoredSignalSession:
    """Serialisable snapshot of a Signal-Protocol session.

    Mirrors the field set of :class:`aethermesh.security.signal_protocol.SignalSession`
    one-for-one. Stored under ``signal:session:<peer_uhid>`` by
    :class:`aethermesh.security.session_store.KeyValueSignalSessionStore`.
    """

    root_key: bytes = b""
    send_chain_key: Optional[bytes] = None
    recv_chain_key: Optional[bytes] = None
    send_counter: int = 0
    recv_counter: int = 0
    previous_chain_count: int = 0
    my_ephemeral_priv: bytes = b""
    my_ephemeral_pub: bytes = b""
    remote_ephemeral_pub: Optional[bytes] = None
    skipped_message_keys: Dict[str, bytes] = field(default_factory=dict)
    pending_pre_key_message: bool = False
    initiator_identity_key_x25519: bytes = b""
    used_signed_pre_key_id: int = 0
    used_one_time_pre_key_id: int = 0
