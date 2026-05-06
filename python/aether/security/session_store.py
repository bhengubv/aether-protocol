"""Persistent storage for Signal-Protocol session state.

Each session is keyed by the peer's UHID. Implementations are responsible
for atomicity and durability — the protocol layer hands an opaque
:class:`StoredSignalSession` in and trusts that :meth:`SignalSessionStore.load`
later returns the exact same state (or ``None`` if no session was previously
stored).

Two reference implementations ship:

* :class:`InMemorySignalSessionStore` — process-local. Used by tests that
  want to exercise the persistence path without touching disk.
* :class:`KeyValueSignalSessionStore` — wraps an arbitrary
  :class:`aether.storage.KeyValueStore`. Sessions are encoded as JSON
  under ``signal:session:<peer_uhid>``.

JSON shape mirrors the C# ``SignalSessionDto`` field-for-field — bytes go
over the wire as base64 strings. Cross-language compatibility is not a
requirement (sessions are inherently per-host), but the parallel shape
keeps the codebases aligned and makes bug-for-bug comparison easy.
"""

from __future__ import annotations

import base64
import json
from abc import ABC, abstractmethod
from typing import Dict, List, Optional

from aether.security.dtos import StoredSignalSession
from aether.storage.kv import KeyValueStore


_SESSION_PREFIX = "signal:session:"


def _b64(b: Optional[bytes]) -> Optional[str]:
    return base64.b64encode(b).decode("ascii") if b is not None else None


def _ub64(s: Optional[str]) -> Optional[bytes]:
    return base64.b64decode(s.encode("ascii")) if s is not None else None


def serialize_session(session: StoredSignalSession) -> bytes:
    """Encode a :class:`StoredSignalSession` to JSON bytes.

    Uses the same property names as the C# ``SignalSessionDto`` so
    on-disk artefacts are visually identical between language ports.
    """
    if session is None:
        raise ValueError("session cannot be None")
    payload = {
        "rk": _b64(session.root_key),
        "cks": _b64(session.send_chain_key),
        "ckr": _b64(session.recv_chain_key),
        "ns": session.send_counter,
        "nr": session.recv_counter,
        "pn": session.previous_chain_count,
        "dhs_priv": _b64(session.my_ephemeral_priv),
        "dhs_pub": _b64(session.my_ephemeral_pub),
        "dhr": _b64(session.remote_ephemeral_pub),
        "mkskipped": {k: _b64(v) for k, v in session.skipped_message_keys.items()},
        "pending_pkmsg": session.pending_pre_key_message,
        "init_ik": _b64(session.initiator_identity_key_x25519),
        "used_spk_id": session.used_signed_pre_key_id,
        "used_opk_id": session.used_one_time_pre_key_id,
    }
    return json.dumps(payload, separators=(",", ":")).encode("utf-8")


def deserialize_session(data: bytes) -> Optional[StoredSignalSession]:
    """Decode JSON bytes back into a :class:`StoredSignalSession`.

    Returns ``None`` if the input is empty. Missing fields default to
    their zero values — same forward-compatibility contract as the C#
    deserializer.
    """
    if data is None:
        raise ValueError("data cannot be None")
    if len(data) == 0:
        return None
    obj = json.loads(data.decode("utf-8"))
    skipped_raw = obj.get("mkskipped") or {}
    skipped: Dict[str, bytes] = {}
    for k, v in skipped_raw.items():
        decoded = _ub64(v)
        if decoded is not None:
            skipped[k] = decoded
    return StoredSignalSession(
        root_key=_ub64(obj.get("rk")) or b"",
        send_chain_key=_ub64(obj.get("cks")),
        recv_chain_key=_ub64(obj.get("ckr")),
        send_counter=int(obj.get("ns", 0)),
        recv_counter=int(obj.get("nr", 0)),
        previous_chain_count=int(obj.get("pn", 0)),
        my_ephemeral_priv=_ub64(obj.get("dhs_priv")) or b"",
        my_ephemeral_pub=_ub64(obj.get("dhs_pub")) or b"",
        remote_ephemeral_pub=_ub64(obj.get("dhr")),
        skipped_message_keys=skipped,
        pending_pre_key_message=bool(obj.get("pending_pkmsg", False)),
        initiator_identity_key_x25519=_ub64(obj.get("init_ik")) or b"",
        used_signed_pre_key_id=int(obj.get("used_spk_id", 0)),
        used_one_time_pre_key_id=int(obj.get("used_opk_id", 0)),
    )


class SignalSessionStore(ABC):
    """Abstract async session store. Mirrors C# ``ISignalSessionStore``."""

    @abstractmethod
    async def load(self, peer_uhid: str) -> Optional[StoredSignalSession]: ...

    @abstractmethod
    async def save(self, peer_uhid: str, session: StoredSignalSession) -> None: ...

    @abstractmethod
    async def delete(self, peer_uhid: str) -> None: ...

    @abstractmethod
    async def list_peers(self) -> List[str]: ...


class InMemorySignalSessionStore(SignalSessionStore):
    """Process-local session store backed by a dict.

    Useful in tests that want to verify the persistence path without
    touching disk. Snapshots are deep-copied via the JSON serializer on
    every put/get so that callers can mutate the in-memory session
    afterwards without disturbing the saved snapshot — the same semantics
    as a real persistent store.
    """

    def __init__(self) -> None:
        self._entries: Dict[str, bytes] = {}

    async def load(self, peer_uhid: str) -> Optional[StoredSignalSession]:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        data = self._entries.get(peer_uhid)
        return deserialize_session(data) if data else None

    async def save(self, peer_uhid: str, session: StoredSignalSession) -> None:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if session is None:
            raise ValueError("session cannot be None")
        self._entries[peer_uhid] = serialize_session(session)

    async def delete(self, peer_uhid: str) -> None:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        self._entries.pop(peer_uhid, None)

    async def list_peers(self) -> List[str]:
        return list(self._entries.keys())


class KeyValueSignalSessionStore(SignalSessionStore):
    """Session store backed by an arbitrary :class:`KeyValueStore`.

    Sessions are JSON-encoded under ``signal:session:<peer_uhid>``.
    Hosts that want a different on-disk format compose
    :class:`aether.storage.EncryptedKeyValueStore` on top of the inner KV
    or supply their own :class:`SignalSessionStore` directly.
    """

    def __init__(self, kv: KeyValueStore) -> None:
        if kv is None:
            raise ValueError("kv cannot be None")
        self._kv: KeyValueStore = kv

    async def load(self, peer_uhid: str) -> Optional[StoredSignalSession]:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        data = await self._kv.get(_SESSION_PREFIX + peer_uhid)
        return deserialize_session(data) if data else None

    async def save(self, peer_uhid: str, session: StoredSignalSession) -> None:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if session is None:
            raise ValueError("session cannot be None")
        await self._kv.put(_SESSION_PREFIX + peer_uhid, serialize_session(session))

    async def delete(self, peer_uhid: str) -> None:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        await self._kv.remove(_SESSION_PREFIX + peer_uhid)

    async def list_peers(self) -> List[str]:
        peers: List[str] = []
        async for k in self._kv.list_keys(prefix=_SESSION_PREFIX):
            peers.append(k[len(_SESSION_PREFIX):])
        return peers
