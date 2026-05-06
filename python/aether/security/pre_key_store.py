"""Persistent storage for the long-term identity keys, signed-pre-key
history, and one-time pre-key pool of a Signal-Protocol participant.

All methods are best-effort from the caller's perspective: failures are
logged at the call site but never propagate up the message-flow stack —
the protocol layer continues with the in-memory state. Implementations
do NOT need to be thread-safe; the
:class:`aether.security.signal_protocol.SignalProtocolService` serialises
access through its own pre-key lock before calling.

KV-backed layout (see :class:`KeyValuePreKeyStore` for the full key map):

* ``signal:identity`` — :class:`StoredIdentityKeys` JSON (one blob).
* ``signal:spk-history`` — :class:`StoredSignedPreKeyHistory` JSON (one blob).
* ``signal:opk:<id>`` — :class:`StoredOneTimePreKey` JSON, one entry per id.

OPKs are written as one entry per id rather than one combined blob so
that :meth:`PreKeyStore.consume_one_time_pre_key` is a single
:meth:`KeyValueStore.remove` call without a read-modify-write cycle on the
whole pool.
"""

from __future__ import annotations

import base64
import json
from abc import ABC, abstractmethod
from typing import Dict, Optional

from aether.security.dtos import (
    StoredIdentityKeys,
    StoredOneTimePreKey,
    StoredSignedPreKey,
    StoredSignedPreKeyHistory,
)
from aether.storage.kv import KeyValueStore


_IDENTITY_KEY = "signal:identity"
_SPK_HISTORY_KEY = "signal:spk-history"
_OPK_PREFIX = "signal:opk:"


def _b64(b: Optional[bytes]) -> Optional[str]:
    return base64.b64encode(b).decode("ascii") if b is not None else None


def _ub64(s: Optional[str]) -> Optional[bytes]:
    return base64.b64decode(s.encode("ascii")) if s is not None else None


def _identity_to_json(identity: StoredIdentityKeys) -> bytes:
    return json.dumps({
        "ed_pk": _b64(identity.ed25519_private_key),
        "ed_pub": _b64(identity.ed25519_public_key),
        "x_pk": _b64(identity.x25519_private_key),
        "x_pub": _b64(identity.x25519_public_key),
        "uhid": identity.local_uhid,
    }, separators=(",", ":")).encode("utf-8")


def _identity_from_json(data: bytes) -> Optional[StoredIdentityKeys]:
    if not data:
        return None
    obj = json.loads(data.decode("utf-8"))
    return StoredIdentityKeys(
        ed25519_private_key=_ub64(obj.get("ed_pk")) or b"",
        ed25519_public_key=_ub64(obj.get("ed_pub")) or b"",
        x25519_private_key=_ub64(obj.get("x_pk")) or b"",
        x25519_public_key=_ub64(obj.get("x_pub")) or b"",
        local_uhid=obj.get("uhid"),
    )


def _spk_entry_to_dict(entry: StoredSignedPreKey) -> dict:
    return {
        "id": entry.id,
        "priv": _b64(entry.private_key),
        "pub": _b64(entry.public_key),
        "sig": _b64(entry.signature),
        "at": entry.generated_at_unix_ms,
    }


def _spk_entry_from_dict(d: dict) -> StoredSignedPreKey:
    return StoredSignedPreKey(
        id=int(d["id"]),
        private_key=_ub64(d["priv"]) or b"",
        public_key=_ub64(d["pub"]) or b"",
        signature=_ub64(d["sig"]) or b"",
        generated_at_unix_ms=int(d["at"]),
    )


def _spk_history_to_json(history: StoredSignedPreKeyHistory) -> bytes:
    return json.dumps({
        "entries": [_spk_entry_to_dict(e) for e in history.entries],
    }, separators=(",", ":")).encode("utf-8")


def _spk_history_from_json(data: bytes) -> Optional[StoredSignedPreKeyHistory]:
    if not data:
        return None
    obj = json.loads(data.decode("utf-8"))
    entries = [_spk_entry_from_dict(d) for d in obj.get("entries", [])]
    return StoredSignedPreKeyHistory(entries=entries)


def _opk_to_json(opk: StoredOneTimePreKey) -> bytes:
    return json.dumps({
        "id": opk.id,
        "priv": _b64(opk.private_key),
        "pub": _b64(opk.public_key),
        "issued": opk.issued,
    }, separators=(",", ":")).encode("utf-8")


def _opk_from_json(data: bytes) -> Optional[StoredOneTimePreKey]:
    if not data:
        return None
    obj = json.loads(data.decode("utf-8"))
    return StoredOneTimePreKey(
        id=int(obj["id"]),
        private_key=_ub64(obj["priv"]) or b"",
        public_key=_ub64(obj["pub"]) or b"",
        issued=bool(obj.get("issued", False)),
    )


class PreKeyStore(ABC):
    """Abstract async pre-key store. Mirrors C# ``IPreKeyStore``."""

    @abstractmethod
    async def load_identity(self) -> Optional[StoredIdentityKeys]: ...

    @abstractmethod
    async def save_identity(self, identity: StoredIdentityKeys) -> None: ...

    @abstractmethod
    async def load_signed_pre_keys(self) -> StoredSignedPreKeyHistory: ...

    @abstractmethod
    async def save_signed_pre_keys(self, history: StoredSignedPreKeyHistory) -> None: ...

    @abstractmethod
    async def load_one_time_pre_keys(self) -> Dict[int, StoredOneTimePreKey]: ...

    @abstractmethod
    async def save_one_time_pre_keys(self, pool: Dict[int, StoredOneTimePreKey]) -> None: ...

    @abstractmethod
    async def consume_one_time_pre_key(self, opk_id: int) -> None: ...


class InMemoryPreKeyStore(PreKeyStore):
    """In-memory pre-key store. Lossy across process restarts; useful for tests."""

    def __init__(self) -> None:
        self._identity: Optional[StoredIdentityKeys] = None
        self._history: StoredSignedPreKeyHistory = StoredSignedPreKeyHistory()
        self._opks: Dict[int, StoredOneTimePreKey] = {}

    async def load_identity(self) -> Optional[StoredIdentityKeys]:
        return self._identity

    async def save_identity(self, identity: StoredIdentityKeys) -> None:
        if identity is None:
            raise ValueError("identity cannot be None")
        self._identity = identity

    async def load_signed_pre_keys(self) -> StoredSignedPreKeyHistory:
        return self._history

    async def save_signed_pre_keys(self, history: StoredSignedPreKeyHistory) -> None:
        if history is None:
            raise ValueError("history cannot be None")
        self._history = history

    async def load_one_time_pre_keys(self) -> Dict[int, StoredOneTimePreKey]:
        return dict(self._opks)

    async def save_one_time_pre_keys(self, pool: Dict[int, StoredOneTimePreKey]) -> None:
        if pool is None:
            raise ValueError("pool cannot be None")
        self._opks = dict(pool)

    async def consume_one_time_pre_key(self, opk_id: int) -> None:
        self._opks.pop(opk_id, None)


class KeyValuePreKeyStore(PreKeyStore):
    """Pre-key store backed by an arbitrary :class:`KeyValueStore`.

    Each OPK is its own entry under ``signal:opk:<id>`` so consuming an
    OPK is a single :meth:`KeyValueStore.remove` call rather than a
    read-modify-write of the whole pool.
    """

    def __init__(self, kv: KeyValueStore) -> None:
        if kv is None:
            raise ValueError("kv cannot be None")
        self._kv: KeyValueStore = kv

    async def load_identity(self) -> Optional[StoredIdentityKeys]:
        data = await self._kv.get(_IDENTITY_KEY)
        return _identity_from_json(data) if data else None

    async def save_identity(self, identity: StoredIdentityKeys) -> None:
        if identity is None:
            raise ValueError("identity cannot be None")
        await self._kv.put(_IDENTITY_KEY, _identity_to_json(identity))

    async def load_signed_pre_keys(self) -> StoredSignedPreKeyHistory:
        data = await self._kv.get(_SPK_HISTORY_KEY)
        if not data:
            return StoredSignedPreKeyHistory()
        history = _spk_history_from_json(data)
        return history if history is not None else StoredSignedPreKeyHistory()

    async def save_signed_pre_keys(self, history: StoredSignedPreKeyHistory) -> None:
        if history is None:
            raise ValueError("history cannot be None")
        await self._kv.put(_SPK_HISTORY_KEY, _spk_history_to_json(history))

    async def load_one_time_pre_keys(self) -> Dict[int, StoredOneTimePreKey]:
        pool: Dict[int, StoredOneTimePreKey] = {}
        async for key in self._kv.list_keys(prefix=_OPK_PREFIX):
            data = await self._kv.get(key)
            if data is None:
                continue
            opk = _opk_from_json(data)
            if opk is not None:
                pool[opk.id] = opk
        return pool

    async def save_one_time_pre_keys(self, pool: Dict[int, StoredOneTimePreKey]) -> None:
        if pool is None:
            raise ValueError("pool cannot be None")
        # Diff against existing keys so removed OPKs (consumed elsewhere)
        # are also removed from the store.
        existing_ids: set[int] = set()
        async for key in self._kv.list_keys(prefix=_OPK_PREFIX):
            tail = key[len(_OPK_PREFIX):]
            try:
                existing_ids.add(int(tail))
            except ValueError:
                continue
        for opk_id, opk in pool.items():
            await self._kv.put(_OPK_PREFIX + str(opk_id), _opk_to_json(opk))
            existing_ids.discard(opk_id)
        for stale_id in existing_ids:
            await self._kv.remove(_OPK_PREFIX + str(stale_id))

    async def consume_one_time_pre_key(self, opk_id: int) -> None:
        await self._kv.remove(_OPK_PREFIX + str(opk_id))
