# SPDX-License-Identifier: MIT
"""In-memory aether-vault service (Phase-2 extension).

Erasure-coded distributed backup over this package's :class:`ReedSolomonCodec`.
Port of the C# reference (``AetherNet.Vault.InMemoryVaultService``) — K=10 / M=4,
shard layout byte-identical so a shard set produced here is decodable by any
other node.
"""
from __future__ import annotations

import hashlib
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, List

from aethernet.vault.reed_solomon import ReedSolomonCodec

VAULT_K = 10
VAULT_M = 4


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


def _sha256_hex(data: bytes) -> str:
    return hashlib.sha256(bytes(data)).hexdigest()


@dataclass
class VaultManifest:
    """The only thing the owner must retain to reconstruct a vaulted file."""

    content_hash: str = ""  # SHA-256 hex of the plaintext
    shard_hashes: List[str] = field(default_factory=list)  # SHA-256 hex of each K+M shard
    k: int = VAULT_K
    m: int = VAULT_M
    size_bytes: int = 0
    label: str = ""
    created_at_utc: datetime = field(default_factory=_now_utc)

    @property
    def total_shards(self) -> int:
        return self.k + self.m


@dataclass
class VaultHealth:
    """A current reachability report for a vaulted file."""

    total_shards: int = 0
    reachable_shards: int = 0
    is_recoverable: bool = False
    redundancy_score: float = 0.0


class IVaultService(ABC):
    """The aether-vault erasure-coded backup store."""

    @abstractmethod
    async def store(self, data: bytes, label: str) -> VaultManifest: ...

    @abstractmethod
    async def recover(self, manifest: VaultManifest) -> bytes: ...

    @abstractmethod
    def check_health(self, manifest: VaultManifest) -> VaultHealth: ...

    @abstractmethod
    async def replicate(self, manifest: VaultManifest, target_redundancy: int = 14) -> None: ...


class InMemoryVaultService(IVaultService):
    """In-memory IVaultService for testing / single-node use; shards lost on restart."""

    def __init__(self) -> None:
        self._shards: Dict[str, bytes] = {}  # shard content hash -> bytes
        self._lock = threading.Lock()

    async def store(self, data: bytes, label: str) -> VaultManifest:
        content_hash = _sha256_hex(data)
        codec = ReedSolomonCodec(VAULT_K, VAULT_M)

        if len(data) == 0:
            # Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
            shards = codec.encode([bytearray(1) for _ in range(VAULT_K)])
        else:
            shards = codec.encode_data(bytes(data))

        shard_hashes: List[str] = []
        with self._lock:
            for sh in shards:
                h = _sha256_hex(sh)
                self._shards[h] = bytes(sh)
                shard_hashes.append(h)

        return VaultManifest(
            content_hash=content_hash,
            shard_hashes=shard_hashes,
            k=VAULT_K,
            m=VAULT_M,
            size_bytes=len(data),
            label=label,
            created_at_utc=_now_utc(),
        )

    async def recover(self, manifest: VaultManifest) -> bytes:
        total = len(manifest.shard_hashes)
        k = manifest.k
        m = total - k
        codec = ReedSolomonCodec(k, m)

        available: Dict[int, bytes] = {}
        with self._lock:
            for i, h in enumerate(manifest.shard_hashes):
                sh = self._shards.get(h)
                if sh is not None:
                    available[i] = sh
        if len(available) < k:
            raise ValueError(
                f"vault: cannot recover — only {len(available)}/{k} shards available"
            )
        return bytes(codec.reconstruct_data(available, manifest.size_bytes))

    def check_health(self, manifest: VaultManifest) -> VaultHealth:
        with self._lock:
            reachable = sum(1 for h in manifest.shard_hashes if h in self._shards)
        total = manifest.total_shards
        return VaultHealth(
            total_shards=total,
            reachable_shards=reachable,
            is_recoverable=reachable >= manifest.k,
            redundancy_score=(reachable / total) if total > 0 else 0.0,
        )

    async def replicate(self, manifest: VaultManifest, target_redundancy: int = 14) -> None:
        # No-op in the in-memory implementation.
        return None
