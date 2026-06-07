# SPDX-License-Identifier: MIT

"""ContentDescriptor - cross-language stable manifest for chunked content.

Wire shape (JSON, snake_case) must match the C# ContentDescriptor
for cross-language byte equality. Added in v1.2.0.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from datetime import datetime
from typing import List

from aethernet import constants


_DEFAULT_CHUNK_SIZE_BYTES = getattr(constants, "DEFAULT_CHUNK_SIZE_BYTES", 65536)


@dataclass
class ContentDescriptor:
    """Manifest for a piece of chunked content.

    Identifies the content by a root hash computed over the per-chunk hashes,
    declares the chunk layout, and lets receivers verify each chunk
    independently as it arrives.

    Wire shape (JSON, snake_case): cross-language stable. Producers can publish
    a descriptor once and any node can pull chunks and verify against it
    without trusting the sender - content addressing makes the descriptor
    itself the authority.
    """

    root_hash: str = ""
    name: str = ""
    total_bytes: int = 0
    chunk_size_bytes: int = _DEFAULT_CHUNK_SIZE_BYTES
    chunk_count: int = 0
    chunk_hashes: List[str] = field(default_factory=list)
    content_type: str = "application/octet-stream"
    created_at: datetime = field(default_factory=datetime.utcnow)

    @staticmethod
    def from_bytes(
        name: str,
        data: bytes,
        content_type: str = "application/octet-stream",
        chunk_size_bytes: int = 0,
    ) -> "ContentDescriptor":
        """Build a descriptor from a buffer.

        Splits into ``chunk_size_bytes``-sized chunks (except the trailing chunk,
        which may be smaller), hashes each, and computes the root over the
        chunk-hash concatenation.
        """
        if chunk_size_bytes <= 0:
            chunk_size_bytes = _DEFAULT_CHUNK_SIZE_BYTES
        total = len(data)
        chunk_count = (total + chunk_size_bytes - 1) // chunk_size_bytes if total else 0
        hashes: List[str] = []
        concat = bytearray()
        for i in range(chunk_count):
            start = i * chunk_size_bytes
            end = min(start + chunk_size_bytes, total)
            digest = hashlib.sha256(data[start:end]).digest()
            hashes.append(digest.hex())
            concat.extend(digest)
        root = (
            hashlib.sha256(bytes(concat)).hexdigest()
            if chunk_count
            else hashlib.sha256(b"").hexdigest()
        )
        return ContentDescriptor(
            root_hash=root,
            name=name,
            total_bytes=total,
            chunk_size_bytes=chunk_size_bytes,
            chunk_count=chunk_count,
            chunk_hashes=hashes,
            content_type=content_type,
        )

    def verify_chunk(self, chunk_index: int, chunk_bytes: bytes) -> bool:
        if chunk_index < 0 or chunk_index >= len(self.chunk_hashes):
            return False
        return hashlib.sha256(chunk_bytes).hexdigest() == self.chunk_hashes[chunk_index]

    def verify_self(self) -> bool:
        """Recompute the root hash over chunk_hashes and compare. Detects manifest tampering."""
        if len(self.chunk_hashes) != self.chunk_count:
            return False
        concat = bytearray()
        for h in self.chunk_hashes:
            try:
                b = bytes.fromhex(h)
            except ValueError:
                return False
            if len(b) != 32:
                return False
            concat.extend(b)
        if not self.chunk_hashes:
            return self.root_hash == hashlib.sha256(b"").hexdigest()
        return hashlib.sha256(bytes(concat)).hexdigest() == self.root_hash

    # ---- JSON wire (snake_case) ----

    def to_wire_dict(self) -> dict:
        if self.created_at.tzinfo is None:
            created_iso = self.created_at.isoformat() + "Z"
        else:
            created_iso = self.created_at.isoformat()
        return {
            "root_hash": self.root_hash,
            "name": self.name,
            "total_bytes": self.total_bytes,
            "chunk_size_bytes": self.chunk_size_bytes,
            "chunk_count": self.chunk_count,
            "chunk_hashes": list(self.chunk_hashes),
            "content_type": self.content_type,
            "created_at": created_iso,
        }

    @staticmethod
    def from_wire_dict(d: dict) -> "ContentDescriptor":
        created_raw = d.get("created_at")
        created_at = datetime.utcnow()
        if isinstance(created_raw, str) and created_raw:
            cleaned = created_raw.rstrip("Z")
            try:
                created_at = datetime.fromisoformat(cleaned)
            except ValueError:
                pass
        return ContentDescriptor(
            root_hash=str(d.get("root_hash", "")),
            name=str(d.get("name", "")),
            total_bytes=int(d.get("total_bytes", 0)),
            chunk_size_bytes=int(d.get("chunk_size_bytes", _DEFAULT_CHUNK_SIZE_BYTES)),
            chunk_count=int(d.get("chunk_count", 0)),
            chunk_hashes=list(d.get("chunk_hashes", []) or []),
            content_type=str(d.get("content_type", "application/octet-stream")),
            created_at=created_at,
        )
