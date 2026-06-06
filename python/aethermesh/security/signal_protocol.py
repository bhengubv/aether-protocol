# SPDX-License-Identifier: MIT

"""Signal Protocol implementation: X3DH session establishment + full
Double Ratchet (Signal §5).

Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
  - DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
  - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
  - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
  - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)

Initial root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).

Double Ratchet (§5): each side maintains a current X25519 ratchet
keypair. Whenever a peer message bears a new ratchet public key, the
receiver does a DH-ratchet step: derive a new chain key via
KDF_RK(RK, DH(myDHs_priv, newDHr)), then generate a fresh DHs and
derive its sending chain via KDF_RK(RK, DH(newDHs_priv, newDHr)).
Signal-canonical X3DH integration: the initiator's X3DH ephemeral key
becomes its first DH-ratchet keypair.

Symmetric ratchet (§5.1): HMAC-SHA256, single-byte domain separation
  (0x01 -> message key, 0x02 -> next chain key).
Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
Identity signing: Ed25519 via Ed25519SigningService.
"""

import asyncio
import hmac as stdlib_hmac
import logging
import os
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Callable, Deque, Dict, List, Optional, Tuple
from cryptography.hazmat.primitives import hashes, hmac
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.serialization import (
    Encoding,
    PrivateFormat,
    PublicFormat,
    NoEncryption,
)
from cryptography.hazmat.backends import default_backend

from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.dtos import (
    StoredIdentityKeys,
    StoredOneTimePreKey,
    StoredSignalSession,
    StoredSignedPreKey,
    StoredSignedPreKeyHistory,
)
from aethermesh.security.pre_key_store import PreKeyStore
from aethermesh.security.session_store import SignalSessionStore
from aethermesh.constants import MAX_SKIPPED_KEYS, AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE


_LOGGER = logging.getLogger(__name__)


# Message-type constants for EncryptedPayload.
MESSAGE_TYPE_NORMAL = 0
MESSAGE_TYPE_PRE_KEY = 1

# HKDF info strings for X3DH session establishment. The SAME info strings
# are used on initiator and responder sides; the responder SWAPS send/recv
# assignment so the initiator's send chain matches the responder's recv
# chain (and vice versa) — but only at session-bootstrap. After the first
# DH-ratchet step on receive, both sides converge on the canonical Signal
# §5 KDF_RK and these initial chain keys are discarded.
#
# These MUST match the C# reference exactly — any drift breaks cross-language
# interop (verified by fixtures/signal/expected/x3dh_basic.json).
_HKDF_ROOT_INFO = b"aether-x3dh-root-v1"
_HKDF_CHAIN_INITIATOR_SEND_INFO = b"aether-chain-initiator-send-v1"
_HKDF_CHAIN_INITIATOR_RECV_INFO = b"aether-chain-initiator-recv-v1"

# HKDF info string for KDF_RK (Signal §5.2). Each DH-ratchet step derives
# a 64-byte block, split into the new root key (first 32 bytes) and the
# new chain key (second 32 bytes). Salt is the current root key.
_HKDF_RATCHET_INFO = b"aether-ratchet-rk-v1"

_X25519_PUBLIC_KEY_SIZE = 32
_X25519_PRIVATE_KEY_SIZE = 32

# Default size of the one-time pre-key pool. Mirrors Signal's published
# guidance and the C# reference (``SignalProtocolService.DefaultOpkPoolSize
# = 100``): ~100 OPKs per device so realistic concurrent-initiator loads
# do not collide on a single shared id.
DEFAULT_OPK_POOL_SIZE = 100

# Maximum number of attempts to pick a non-colliding random OPK id during
# pool top-up. Collisions in a 100-element pool over 2^31 ids are
# statistically negligible but we still guard explicitly.
_MAX_OPK_ID_ALLOC_ATTEMPTS = 64


@dataclass
class PreKeyBundle:
    """Pre-key bundle published by a node so others can initiate X3DH sessions.

    Two identity keys per node — Ed25519 for signing and X25519 for ECDH.
    Keeping them separate (rather than using XEdDSA) is the simpler choice
    across the 8-language implementation family.
    """

    uhid: str
    identity_key: bytes  # Ed25519 public key (32 bytes)
    identity_key_x25519: bytes  # X25519 public key (32 bytes raw)
    pre_key_id: int
    pre_key: bytes  # X25519 public key (32 bytes raw)
    signed_pre_key_id: int
    signed_pre_key: bytes  # X25519 public key (32 bytes raw)
    signed_pre_key_signature: bytes  # Ed25519 signature (64 bytes)


@dataclass
class EncryptedPayload:
    """Wire-level form of an encrypted message.

    Two layered ratchets contribute fields:

    1. **X3DH session establishment** (Signal §3) — populated only on the
       first message a new initiator sends to a peer (message_type == 1):
       ``initiator_identity_key_x25519``, ``used_signed_pre_key_id``,
       ``used_one_time_pre_key_id``. The responder uses these to run X3DH
       on its side and derive the same root key.

    2. **Double Ratchet** (Signal §5) — ``sender_ephemeral_key_x25519`` and
       ``previous_chain_count`` populated on EVERY message.
       ``sender_ephemeral_key_x25519`` is the sender's current DH-ratchet
       public key; when it changes between messages, the receiver runs a
       DH-ratchet step that re-keys the chain and gives per-roundtrip forward
       secrecy and post-compromise security. On the very first PreKey message,
       this equals the X3DH ephemeral public key (Signal-canonical
       integration: initiator's X3DH ephemeral becomes its first DH-ratchet
       public).

    ``initiator_ephemeral_key_x25519`` is retained for backward compatibility
    with consumers of the pre-Double-Ratchet wire envelope. On PreKey messages
    it equals ``sender_ephemeral_key_x25519``; on normal messages it stays
    None. New consumers should ignore this field and use
    ``sender_ephemeral_key_x25519`` exclusively.
    """

    ciphertext: bytes
    nonce: bytes
    message_type: int
    sender_uhid: str
    counter: int

    initiator_identity_key_x25519: Optional[bytes] = None
    initiator_ephemeral_key_x25519: Optional[bytes] = None
    used_signed_pre_key_id: int = 0
    used_one_time_pre_key_id: int = 0

    # Double Ratchet (Signal §5) — populated on every message.
    sender_ephemeral_key_x25519: Optional[bytes] = None
    previous_chain_count: int = 0


@dataclass
class SignalSession:
    """State of a Signal-Protocol session with a single peer — both X3DH
    session-establishment metadata and Double-Ratchet (Signal §5) state.

    Double-Ratchet state per Signal §5:
      - ``root_key`` — RK. Re-keyed on every DH-ratchet step.
      - ``my_ephemeral_priv`` / ``my_ephemeral_pub`` — DHs, my current ratchet keypair.
      - ``remote_ephemeral_pub`` — DHr, peer's last-known ratchet public key.
        None until first DH-ratchet step.
      - ``send_chain_key`` — CKs, my current sending chain key. None until I've
        sent (or initialized) on this chain.
      - ``recv_chain_key`` — CKr, my current receiving chain key. None until I've
        received on this chain.
      - ``send_counter`` / ``recv_counter`` — Ns / Nr (reset to 0 each DH-ratchet step).
      - ``previous_chain_count`` — PN, number of messages I sent in my previous
        sending chain (so the receiver can compute skipped keys across a
        DH-ratchet boundary).
      - ``skipped_message_keys`` — MKSKIPPED, keyed by ``"hex(DHr_pub):counter"``
        so out-of-order messages from a previous chain (different DHr) can
        still be decrypted via the cache after a DH-ratchet step.

    On the initiator side, ``pending_pre_key_message`` is True until the first
    outbound message is sent. While True, the next encrypt() emits a PreKey
    message carrying the X3DH inputs.
    """

    root_key: bytes = b""

    # Sending chain key. None until first send (or until DH-ratchet rekeys it).
    send_chain_key: Optional[bytes] = None
    # Receiving chain key. None until first receive that triggers a DH-ratchet step.
    recv_chain_key: Optional[bytes] = None

    send_counter: int = 0
    recv_counter: int = 0
    # Number of messages sent in the previous sending chain (Signal §5: PN).
    previous_chain_count: int = 0

    # My current DH-ratchet keypair (X25519, 32 bytes each).
    my_ephemeral_priv: bytes = b""
    my_ephemeral_pub: bytes = b""
    # Peer's last-seen DH-ratchet public key. None until first DH-ratchet step.
    remote_ephemeral_pub: Optional[bytes] = None

    # Skipped message keys keyed by "hex(remote_eph_pub):counter". The
    # remote_eph_pub binding is essential — out-of-order messages from a
    # previous chain (different DHr) can still arrive after a DH-ratchet
    # step, and they need their own per-chain key set.
    skipped_message_keys: Dict[str, bytes] = field(default_factory=dict)

    pending_pre_key_message: bool = False
    initiator_identity_key_x25519: bytes = b""
    used_signed_pre_key_id: int = 0
    used_one_time_pre_key_id: int = 0


@dataclass
class _SignedPreKeyEntry:
    """One signed-pre-key entry in the rotation history.

    The private half is held so that responder-side X3DH can still complete
    when a peer presents a slightly-stale SPK during the rotation window.
    ``generated_at`` is a :class:`datetime` in UTC for fast age comparison
    against the rotation interval.
    """

    id: int
    private_key: bytes
    public_key: bytes
    signature: bytes
    generated_at: datetime


class _PreKeyState:
    """Responder-side pre-key state.

    Holds the private halves of the signed pre-key and one-time pre-keys so
    we can run our side of X3DH when an initiator's PreKey message arrives.

    The OPKs are managed as a pool of ``opk_pool_size`` available keys. New
    bundle generations dequeue the next available id from
    :attr:`available_opk_ids`; the OPK stays in :attr:`one_time_pre_keys`
    until a responder consumes it via X3DH, at which point it is removed
    and never reused. The pool is topped back up to ``opk_pool_size`` on
    every bundle generation. Pre-2026-05-05 the responder held a SINGLE
    OPK in this dict, which silently dropped legitimate concurrent
    initiators after the first consumed it.

    Signed-pre-key state spans the full rotation history (oldest first,
    newest last). The ``signed_pre_key_*`` fields are denormalised
    references to the active (newest) entry kept around for fast-path
    accessors that don't want to chase the list.
    """

    def __init__(self) -> None:
        self.signed_pre_key_id: int = 0
        self.signed_pre_key_priv: bytes = b""
        self.signed_pre_key_pub: bytes = b""
        self.signed_pre_key_signature: bytes = b""
        # Full rotation history; oldest first. Active SPK is history[-1].
        self.signed_pre_key_history: List[_SignedPreKeyEntry] = []
        # int -> (priv, pub). Each entry is removed on consumption.
        self.one_time_pre_keys: Dict[int, Tuple[bytes, bytes]] = {}
        # FIFO of OPK ids that are present in ``one_time_pre_keys`` AND
        # have not yet been advertised to a peer in any bundle. New bundles
        # popleft() from here; top-up appends.
        self.available_opk_ids: Deque[int] = deque()


@dataclass
class SignedPreKeyRotationOptions:
    """Configuration for periodic signed-pre-key rotation.

    Signal §3.3 recommends rotating SPKs periodically (their server-side
    documentation suggests weekly). On every :meth:`generate_pre_key_bundle`
    call the service checks whether the active SPK is older than
    :attr:`rotation_interval`; if it is, a fresh SPK is generated and the
    old one is appended to the history. The history is then trimmed to keep
    at most :attr:`retained_history_count` prior entries (plus the new
    active one).

    A higher :attr:`retained_history_count` widens the rotation window during
    which messages signed under a recently-rotated SPK still complete X3DH
    on the responder side; a value of 0 means "rotate-and-forget" — every
    rotation immediately invalidates all in-flight PreKey messages under
    the old SPK.

    Defaults: :attr:`rotation_interval` = 7 days, :attr:`retained_history_count` = 3.
    Mirrors :data:`SignedPreKeyRotationOptions.Default` in the C# reference.
    """

    rotation_interval: timedelta = timedelta(days=7)
    retained_history_count: int = 3


def _utcnow() -> datetime:
    """Default ``now`` provider for the rotation-age check.

    Returns a timezone-aware UTC datetime. Tests that want to drive the
    clock pass their own zero-arg callable through the ``now_provider``
    constructor kwarg.
    """
    return datetime.now(timezone.utc)


class SignalProtocolService:
    """Signal Protocol implementation: X3DH + full Double Ratchet (Signal §5).

    One-time pre-keys (OPKs) are issued from a configurable pool — the
    target size of which is set via the ``opk_pool_size`` constructor
    kwarg (default :data:`DEFAULT_OPK_POOL_SIZE`, 100). On every
    :meth:`generate_pre_key_bundle` call the pool is topped up to that
    size; on every :meth:`decrypt` of a PreKey message the consumed OPK
    is atomically removed under a single ``asyncio.Lock`` shared with
    the bundle-generation path.

    Persistence: when a ``session_store`` and/or ``pre_key_store`` is wired
    up, the long-term identity, SPK history, OPK pool, and per-peer
    Double-Ratchet sessions all survive a process restart. After every
    state mutation the service fires-and-forgets an ``asyncio.create_task``
    that writes the snapshot to the store; failures are logged at WARN
    level and never block the message flow. Stores can be either
    :class:`aethermesh.security.session_store.InMemorySignalSessionStore` /
    :class:`aethermesh.security.pre_key_store.InMemoryPreKeyStore` (volatile,
    for tests) or :class:`aethermesh.security.session_store.KeyValueSignalSessionStore`
    / :class:`aethermesh.security.pre_key_store.KeyValuePreKeyStore` over an
    :class:`aethermesh.storage.KeyValueStore` (durable).

    Constructor wiring:

    * ``opk_pool_size`` — target size of the OPK pool. Default 100.
    * ``session_store`` — persistence for per-peer sessions. ``None`` means
      "no persistence" (sessions are lost on restart).
    * ``pre_key_store`` — persistence for identity + SPK history + OPK pool.
      ``None`` means "no persistence" (everything is regenerated on restart).
    * ``rotation_options`` — SPK rotation policy. Defaults to 7-day
      rotation with a 3-deep retained history.
    * ``now_provider`` — zero-arg callable returning a :class:`datetime` in
      UTC. Tests use a mutable clock. Defaults to ``datetime.now(UTC)``.
    """

    def __init__(
        self,
        opk_pool_size: int = DEFAULT_OPK_POOL_SIZE,
        session_store: Optional[SignalSessionStore] = None,
        pre_key_store: Optional[PreKeyStore] = None,
        rotation_options: Optional[SignedPreKeyRotationOptions] = None,
        now_provider: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if opk_pool_size < 1:
            raise ValueError(
                f"opk_pool_size must be >= 1 (got {opk_pool_size})."
            )
        self._opk_pool_size: int = opk_pool_size

        # Resolve rotation options + clock with validation. Mirrors the
        # C# ctor checks (RotationInterval > 0, RetainedHistoryCount >= 0).
        opts = rotation_options if rotation_options is not None else SignedPreKeyRotationOptions()
        if opts.rotation_interval <= timedelta(0):
            raise ValueError("rotation_options.rotation_interval must be > 0.")
        if opts.retained_history_count < 0:
            raise ValueError("rotation_options.retained_history_count must be >= 0.")
        self._rotation_options: SignedPreKeyRotationOptions = opts
        self._now_provider: Callable[[], datetime] = now_provider or _utcnow

        self._sessions: Dict[str, SignalSession] = {}

        # Long-term identity keys — two distinct keypairs per node.
        # X25519 for ECDH (X3DH); Ed25519 for signing.
        self._identity_x25519_priv: bytes = b""
        self._identity_x25519_pub: bytes = b""
        self._ed25519_private_key: bytes = b""
        self._ed25519_public_key: bytes = b""

        # Local UHID — captured when generate_pre_key_bundle is called or
        # via set_local_uhid. Stamped on outbound EncryptedPayloads.
        self._local_uhid: Optional[str] = None

        # Pre-key state held for responder-side X3DH.
        self._pre_keys: _PreKeyState = _PreKeyState()

        # Single lock guarding OPK pool mutations — the bundle-generation
        # dequeue and the responder-side consume MUST be atomic relative
        # to one another, otherwise concurrent initiators using the same
        # bundle id race the responder-side consume and one of them gets
        # a "PreKey message references one-time pre-key id X which is not
        # held" error.
        self._opk_lock: asyncio.Lock = asyncio.Lock()

        self._session_store: Optional[SignalSessionStore] = session_store
        self._pre_key_store: Optional[PreKeyStore] = pre_key_store

        self._initialize_identity_keys()
        # Hydrate from stores synchronously: identity overrides the
        # freshly-generated keys when present. Each await uses a fresh
        # event loop via asyncio.run if there's no running loop, but we
        # know our stores' implementations are pure-Python and don't
        # block, so we drive them via asyncio.run / loop.run_until_complete.
        self._hydrate_from_pre_key_store_sync()
        self._hydrate_from_session_store_sync()

    def _initialize_identity_keys(self) -> None:
        """Generate the long-term X25519 + Ed25519 identity keypairs."""
        # Ed25519 for signing.
        self._ed25519_private_key, self._ed25519_public_key = (
            Ed25519SigningService.generate_keypair()
        )

        # X25519 for X3DH ECDH.
        priv = X25519PrivateKey.generate()
        self._identity_x25519_priv = priv.private_bytes(
            encoding=Encoding.Raw,
            format=PrivateFormat.Raw,
            encryption_algorithm=NoEncryption(),
        )
        self._identity_x25519_pub = priv.public_key().public_bytes(
            encoding=Encoding.Raw,
            format=PublicFormat.Raw,
        )

    # ─── Persistence: hydration ──────────────────────────────────────────

    @staticmethod
    def _run_async(coro):
        """Run an awaitable synchronously from constructor context.

        Constructor code runs synchronously but we need to await on the
        store implementations (which are async to match their KV-backed
        kin). The C# reference uses ``GetAwaiter().GetResult()`` — Python's
        equivalent depends on whether an event loop is already running:

        * No running loop (typical sync construction) — :func:`asyncio.run`
          spins up a fresh loop, drives the coroutine to completion, and
          tears the loop down.
        * Running loop (constructed from inside an ``async def`` function,
          e.g. inside a pytest-asyncio test) — we cannot start a nested
          loop on the same thread, so run the coroutine on a worker
          thread that owns its own loop, and block the calling thread on
          the result. This still preserves the
          "constructor returns a fully hydrated service" contract.
        """
        try:
            asyncio.get_running_loop()
        except RuntimeError:
            return asyncio.run(coro)

        # Already inside an event loop — drive on a worker thread so we
        # don't deadlock trying to nest event loops on the same OS thread.
        import concurrent.futures
        import threading

        result_holder: Dict[str, object] = {}
        error_holder: Dict[str, BaseException] = {}

        def _runner() -> None:
            try:
                result_holder["v"] = asyncio.run(coro)
            except BaseException as exc:  # noqa: BLE001
                error_holder["e"] = exc

        worker = threading.Thread(target=_runner, daemon=True)
        worker.start()
        worker.join()
        if "e" in error_holder:
            raise error_holder["e"]
        return result_holder.get("v")

    def _hydrate_from_pre_key_store_sync(self) -> None:
        """Load identity, SPK history, and OPK pool from the pre-key store.

        If no identity is persisted, the freshly generated one is saved.
        If a partial history exists, the active SPK is denormalised onto
        :attr:`_pre_keys` for fast-path lookups. OPKs marked ``issued`` are
        kept in the pool but NOT enqueued onto :attr:`_pre_keys.available_opk_ids`
        so the next bundle generation issues a fresh OPK rather than
        re-handing-out an already-published id.
        """
        if self._pre_key_store is None:
            return
        try:
            stored_identity = self._run_async(self._pre_key_store.load_identity())
            if stored_identity is not None:
                self._ed25519_private_key = bytes(stored_identity.ed25519_private_key)
                self._ed25519_public_key = bytes(stored_identity.ed25519_public_key)
                self._identity_x25519_priv = bytes(stored_identity.x25519_private_key)
                self._identity_x25519_pub = bytes(stored_identity.x25519_public_key)
                if stored_identity.local_uhid:
                    self._local_uhid = stored_identity.local_uhid
            else:
                self._run_async(self._pre_key_store.save_identity(self._snapshot_identity()))

            history = self._run_async(self._pre_key_store.load_signed_pre_keys())
            self._pre_keys.signed_pre_key_history.clear()
            for entry in sorted(history.entries, key=lambda e: e.generated_at_unix_ms):
                self._pre_keys.signed_pre_key_history.append(_SignedPreKeyEntry(
                    id=entry.id,
                    private_key=bytes(entry.private_key),
                    public_key=bytes(entry.public_key),
                    signature=bytes(entry.signature),
                    generated_at=datetime.fromtimestamp(
                        entry.generated_at_unix_ms / 1000.0, tz=timezone.utc),
                ))
            if self._pre_keys.signed_pre_key_history:
                active = self._pre_keys.signed_pre_key_history[-1]
                self._pre_keys.signed_pre_key_id = active.id
                self._pre_keys.signed_pre_key_priv = active.private_key
                self._pre_keys.signed_pre_key_pub = active.public_key
                self._pre_keys.signed_pre_key_signature = active.signature

            opks = self._run_async(self._pre_key_store.load_one_time_pre_keys())
            self._pre_keys.one_time_pre_keys.clear()
            self._pre_keys.available_opk_ids.clear()
            for opk_id, opk in opks.items():
                self._pre_keys.one_time_pre_keys[opk_id] = (
                    bytes(opk.private_key), bytes(opk.public_key))
                if not opk.issued:
                    self._pre_keys.available_opk_ids.append(opk_id)
        except Exception as exc:
            _LOGGER.warning(
                "Failed to hydrate pre-key state; continuing with freshly-generated keys: %s",
                exc, exc_info=True)

    def _hydrate_from_session_store_sync(self) -> None:
        """Load every persisted session into :attr:`_sessions`.

        Each session is a Double-Ratchet snapshot — see
        :class:`aethermesh.security.dtos.StoredSignalSession`. Failures on a
        single peer are logged and skipped; the rest of the peer set
        still hydrates.
        """
        if self._session_store is None:
            return
        try:
            peers = self._run_async(self._session_store.list_peers())
            for peer_uhid in peers:
                try:
                    stored = self._run_async(self._session_store.load(peer_uhid))
                    if stored is not None:
                        self._sessions[peer_uhid] = self._session_from_stored(stored)
                except Exception as exc:
                    _LOGGER.warning(
                        "Failed to load session for %s; skipping: %s",
                        peer_uhid, exc)
        except Exception as exc:
            _LOGGER.warning(
                "Failed to enumerate sessions from store: %s", exc, exc_info=True)

    def _snapshot_identity(self) -> StoredIdentityKeys:
        """Snapshot the current identity for persistence."""
        return StoredIdentityKeys(
            ed25519_private_key=bytes(self._ed25519_private_key),
            ed25519_public_key=bytes(self._ed25519_public_key),
            x25519_private_key=bytes(self._identity_x25519_priv),
            x25519_public_key=bytes(self._identity_x25519_pub),
            local_uhid=self._local_uhid,
        )

    def _snapshot_session(self, session: "SignalSession") -> StoredSignalSession:
        """Snapshot a live :class:`SignalSession` to a persistence DTO.

        Bytes are wrapped via :class:`bytes` (not aliased) so the saved
        snapshot is decoupled from subsequent mutation of the live
        session object.
        """
        return StoredSignalSession(
            root_key=bytes(session.root_key),
            send_chain_key=bytes(session.send_chain_key) if session.send_chain_key else None,
            recv_chain_key=bytes(session.recv_chain_key) if session.recv_chain_key else None,
            send_counter=session.send_counter,
            recv_counter=session.recv_counter,
            previous_chain_count=session.previous_chain_count,
            my_ephemeral_priv=bytes(session.my_ephemeral_priv),
            my_ephemeral_pub=bytes(session.my_ephemeral_pub),
            remote_ephemeral_pub=(
                bytes(session.remote_ephemeral_pub)
                if session.remote_ephemeral_pub else None),
            skipped_message_keys={k: bytes(v) for k, v in session.skipped_message_keys.items()},
            pending_pre_key_message=session.pending_pre_key_message,
            initiator_identity_key_x25519=bytes(session.initiator_identity_key_x25519),
            used_signed_pre_key_id=session.used_signed_pre_key_id,
            used_one_time_pre_key_id=session.used_one_time_pre_key_id,
        )

    @staticmethod
    def _session_from_stored(stored: StoredSignalSession) -> "SignalSession":
        """Inverse of :meth:`_snapshot_session`."""
        return SignalSession(
            root_key=bytes(stored.root_key),
            send_chain_key=bytes(stored.send_chain_key) if stored.send_chain_key else None,
            recv_chain_key=bytes(stored.recv_chain_key) if stored.recv_chain_key else None,
            send_counter=stored.send_counter,
            recv_counter=stored.recv_counter,
            previous_chain_count=stored.previous_chain_count,
            my_ephemeral_priv=bytes(stored.my_ephemeral_priv),
            my_ephemeral_pub=bytes(stored.my_ephemeral_pub),
            remote_ephemeral_pub=(
                bytes(stored.remote_ephemeral_pub)
                if stored.remote_ephemeral_pub else None),
            skipped_message_keys={k: bytes(v) for k, v in stored.skipped_message_keys.items()},
            pending_pre_key_message=stored.pending_pre_key_message,
            initiator_identity_key_x25519=bytes(stored.initiator_identity_key_x25519),
            used_signed_pre_key_id=stored.used_signed_pre_key_id,
            used_one_time_pre_key_id=stored.used_one_time_pre_key_id,
        )

    def _snapshot_signed_pre_keys(self) -> StoredSignedPreKeyHistory:
        """Snapshot the SPK history for persistence."""
        entries: List[StoredSignedPreKey] = []
        for entry in self._pre_keys.signed_pre_key_history:
            entries.append(StoredSignedPreKey(
                id=entry.id,
                private_key=bytes(entry.private_key),
                public_key=bytes(entry.public_key),
                signature=bytes(entry.signature),
                generated_at_unix_ms=int(entry.generated_at.timestamp() * 1000),
            ))
        return StoredSignedPreKeyHistory(entries=entries)

    def _snapshot_one_time_pre_keys(self) -> Dict[int, StoredOneTimePreKey]:
        """Snapshot the OPK pool for persistence.

        ``issued`` is True iff this OPK has been advertised in some bundle
        but not yet consumed — i.e. is in :attr:`_pre_keys.one_time_pre_keys`
        but NOT in :attr:`_pre_keys.available_opk_ids`. Consumed OPKs are
        already gone from both structures by the time this runs.
        """
        available = set(self._pre_keys.available_opk_ids)
        out: Dict[int, StoredOneTimePreKey] = {}
        for opk_id, (priv, pub) in self._pre_keys.one_time_pre_keys.items():
            out[opk_id] = StoredOneTimePreKey(
                id=opk_id,
                private_key=bytes(priv),
                public_key=bytes(pub),
                issued=opk_id not in available,
            )
        return out

    # ─── Persistence: best-effort fire-and-forget saves ────────────────

    def _persist_session(self, peer_uhid: str, session: "SignalSession") -> None:
        """Schedule a fire-and-forget persistence save for a session.

        The snapshot is taken synchronously (so subsequent mutations of
        the live session don't bleed into the saved blob); the actual
        write is dispatched onto the running event loop and awaited
        elsewhere. Failures log at WARN and never propagate.
        """
        if self._session_store is None:
            return
        try:
            snapshot = self._snapshot_session(session)
            self._dispatch_save(self._session_store.save(peer_uhid, snapshot),
                                f"session for {peer_uhid}")
        except Exception as exc:
            _LOGGER.warning("Failed to snapshot session for %s: %s", peer_uhid, exc)

    def _persist_identity(self) -> None:
        """Schedule a fire-and-forget identity save."""
        if self._pre_key_store is None:
            return
        try:
            snapshot = self._snapshot_identity()
            self._dispatch_save(self._pre_key_store.save_identity(snapshot), "identity")
        except Exception as exc:
            _LOGGER.warning("Failed to snapshot identity: %s", exc)

    def _persist_signed_pre_keys(self) -> None:
        """Schedule a fire-and-forget SPK history save. Caller MUST hold ``_opk_lock``."""
        if self._pre_key_store is None:
            return
        try:
            snapshot = self._snapshot_signed_pre_keys()
            self._dispatch_save(self._pre_key_store.save_signed_pre_keys(snapshot),
                                "SPK history")
        except Exception as exc:
            _LOGGER.warning("Failed to snapshot SPK history: %s", exc)

    def _persist_one_time_pre_keys(self) -> None:
        """Schedule a fire-and-forget OPK pool save. Caller MUST hold ``_opk_lock``."""
        if self._pre_key_store is None:
            return
        try:
            snapshot = self._snapshot_one_time_pre_keys()
            self._dispatch_save(self._pre_key_store.save_one_time_pre_keys(snapshot),
                                "OPK pool")
        except Exception as exc:
            _LOGGER.warning("Failed to snapshot OPK pool: %s", exc)

    def _consume_one_time_pre_key_persistent(self, opk_id: int) -> None:
        """Schedule a fire-and-forget single-OPK delete on the persistent store."""
        if self._pre_key_store is None:
            return
        self._dispatch_save(self._pre_key_store.consume_one_time_pre_key(opk_id),
                            f"OPK {opk_id} consume")

    def _dispatch_save(self, coro, label: str) -> None:
        """Dispatch an awaitable as a fire-and-forget task on the running loop.

        If no event loop is running (e.g. construction-time hydration),
        run the coroutine to completion synchronously instead. Either way,
        exceptions log at WARN and never propagate.
        """
        try:
            loop = asyncio.get_running_loop()
        except RuntimeError:
            try:
                asyncio.run(coro)
            except Exception as exc:
                _LOGGER.warning("Failed to persist %s: %s", label, exc)
            return

        async def _run() -> None:
            try:
                await coro
            except Exception as exc:
                _LOGGER.warning("Failed to persist %s: %s", label, exc)

        loop.create_task(_run())

    def set_local_uhid(self, local_uhid: str) -> None:
        """Set the local node's UHID. Required before any encrypt() call.

        Mutating the UHID schedules a best-effort persist of the identity
        record so a subsequent restart does not need a fresh
        :meth:`set_local_uhid` call.
        """
        if not local_uhid:
            raise ValueError("local_uhid cannot be empty")
        previous = self._local_uhid
        self._local_uhid = local_uhid
        if previous != local_uhid:
            self._persist_identity()

    def has_session(self, peer_uhid: str) -> bool:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        return peer_uhid in self._sessions

    async def encrypt(self, peer_uhid: str, plaintext: bytes) -> EncryptedPayload:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if plaintext is None:
            raise ValueError("plaintext cannot be None")

        session = self._sessions.get(peer_uhid)
        if session is None:
            raise ValueError(f"No session established with peer {peer_uhid}")
        if self._local_uhid is None:
            raise ValueError(
                "Local UHID is not set. Call generate_pre_key_bundle(uhid) "
                "or set_local_uhid(uhid) before encrypting."
            )

        # Lazy CKs initialization for the initiator's first send: the X3DH
        # setup placed DHs and DHr but did not derive CKs (the Double
        # Ratchet defers it until first send to avoid an extra KDF step
        # when no message is ever sent on a session).
        if session.send_chain_key is None:
            if session.remote_ephemeral_pub is None:
                raise ValueError(
                    "Cannot derive sending chain: peer's ratchet public key is unknown."
                )
            self._dh_ratchet_send_only(session, session.remote_ephemeral_pub)

        message_key = self._ratchet_send_chain(session)
        try:
            nonce = os.urandom(AES_GCM_NONCE_SIZE)
            cipher = AESGCM(message_key)
            ciphertext = cipher.encrypt(nonce, plaintext, None)

            counter = session.send_counter
            session.send_counter += 1
            ratchet_pub = bytes(session.my_ephemeral_pub)

            # PreKey message? Carries our X3DH inputs so the responder can mirror
            # the DHs and arrive at the same root key.
            if session.pending_pre_key_message:
                payload = EncryptedPayload(
                    ciphertext=ciphertext,
                    nonce=nonce,
                    message_type=MESSAGE_TYPE_PRE_KEY,
                    sender_uhid=self._local_uhid,
                    counter=counter,
                    initiator_identity_key_x25519=bytes(session.initiator_identity_key_x25519),
                    # Backward-compat field — equals sender_ephemeral_key_x25519
                    # on the first message because the initiator's X3DH ephemeral
                    # becomes its first DH-ratchet pubkey.
                    initiator_ephemeral_key_x25519=ratchet_pub,
                    used_signed_pre_key_id=session.used_signed_pre_key_id,
                    used_one_time_pre_key_id=session.used_one_time_pre_key_id,
                    sender_ephemeral_key_x25519=ratchet_pub,
                    previous_chain_count=session.previous_chain_count,
                )
                session.pending_pre_key_message = False
                self._persist_session(peer_uhid, session)
                return payload

            normal = EncryptedPayload(
                ciphertext=ciphertext,
                nonce=nonce,
                message_type=MESSAGE_TYPE_NORMAL,
                sender_uhid=self._local_uhid,
                counter=counter,
                sender_ephemeral_key_x25519=ratchet_pub,
                previous_chain_count=session.previous_chain_count,
            )
            self._persist_session(peer_uhid, session)
            return normal
        finally:
            self._zero_memory(message_key)

    async def decrypt(self, peer_uhid: str, payload: EncryptedPayload) -> bytes:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if payload is None:
            raise ValueError("payload cannot be None")

        # Every Double-Ratchet message carries the sender's current ratchet
        # public key. Fall back to initiator_ephemeral_key_x25519 for backward
        # compatibility with older PreKey messages from peers that haven't
        # upgraded to the new wire envelope.
        sender_ratchet_pub = (
            payload.sender_ephemeral_key_x25519
            or payload.initiator_ephemeral_key_x25519
        )

        # PreKey message? Establish (or replace) the responder-side session
        # before attempting decryption.
        if payload.message_type == MESSAGE_TYPE_PRE_KEY:
            if payload.initiator_identity_key_x25519 is None or sender_ratchet_pub is None:
                raise ValueError(
                    "PreKey message missing initiator key material "
                    "(initiator_identity_key_x25519 and sender_ephemeral_key_x25519 / "
                    "initiator_ephemeral_key_x25519)."
                )
            await self._establish_responder_session(peer_uhid, payload, sender_ratchet_pub)

        session = self._sessions.get(peer_uhid)
        if session is None:
            raise ValueError(f"No session established with peer {peer_uhid}")

        if sender_ratchet_pub is None:
            raise ValueError(
                "Message missing sender_ephemeral_key_x25519 — "
                "required for the Double Ratchet."
            )

        # DH-ratchet step? Triggered when the peer's ratchet public key changes.
        if (
            session.remote_ephemeral_pub is None
            or not stdlib_hmac.compare_digest(sender_ratchet_pub, session.remote_ephemeral_pub)
        ):
            # First, derive any skipped keys from the previous receive chain
            # (the chain keyed by the OLD remote_ephemeral_pub). Then ratchet.
            self._skip_message_keys(session, payload.previous_chain_count)
            self._dh_ratchet_receive(session, sender_ratchet_pub)

        if len(payload.ciphertext) < AES_GCM_TAG_SIZE:
            raise ValueError("Ciphertext too short")

        # Skipped key cached for this (DHr_pub, counter) pair?
        skipped_key = self._skipped_key(sender_ratchet_pub, payload.counter)
        if skipped_key in session.skipped_message_keys:
            message_key = session.skipped_message_keys.pop(skipped_key)
        else:
            if session.recv_chain_key is None:
                raise ValueError(
                    "Receive chain not initialized (DH-ratchet step missing)."
                )

            gap = payload.counter - session.recv_counter
            if gap > MAX_SKIPPED_KEYS:
                raise ValueError(
                    f"Message counter gap ({gap}) exceeds maximum ({MAX_SKIPPED_KEYS}). "
                    "Session must be re-established."
                )

            # Skip ahead, caching intermediate keys.
            while session.recv_counter < payload.counter:
                skip_key = self._ratchet_recv_chain(session)
                session.skipped_message_keys[
                    self._skipped_key(sender_ratchet_pub, session.recv_counter)
                ] = skip_key
                session.recv_counter += 1

            message_key = self._ratchet_recv_chain(session)
            session.recv_counter += 1

        try:
            cipher = AESGCM(message_key)
            plaintext = cipher.decrypt(payload.nonce, payload.ciphertext, None)
            self._persist_session(peer_uhid, session)
            return plaintext
        except Exception as e:
            raise ValueError(f"Decryption failed: {e}")
        finally:
            self._zero_memory(message_key)

    async def generate_pre_key_bundle(self, local_uhid: str) -> PreKeyBundle:
        """Generate a pre-key bundle. Retains the SPK + OPK private halves
        for responder-side X3DH on this node.

        OPKs are issued from the pool: on each call we top the pool up to
        :attr:`_opk_pool_size` available (un-issued) keys, then dequeue
        the next un-issued OPK as the one published in this bundle. The
        OPK then stays in :attr:`_pre_keys.one_time_pre_keys` (no longer
        in the available deque) until a responder consumes it via X3DH.

        Signed pre-keys (Signal §3.3): the active SPK is the
        most-recently-generated entry in :attr:`_pre_keys.signed_pre_key_history`.
        On every call this method checks whether the active SPK is older
        than :attr:`_rotation_options.rotation_interval`; if it is, a fresh
        SPK is generated and the history is rolled forward. The history is
        trimmed to keep at most :attr:`_rotation_options.retained_history_count`
        prior entries (plus the new active one). Messages signed under any
        retained SPK still complete responder-side X3DH; messages signed
        under a pruned SPK fail with a "not held" error.
        """
        if not local_uhid:
            raise ValueError("local_uhid cannot be empty")
        uhid_changed = self._local_uhid != local_uhid
        self._local_uhid = local_uhid
        if uhid_changed:
            self._persist_identity()

        # OPK pool top-up + dequeue: must be atomic relative to the
        # responder-side consume in _establish_responder_session. We hold
        # _opk_lock across both steps.
        history_mutated = False
        async with self._opk_lock:
            # Lazily generate the first SPK or rotate when stale.
            if not self._pre_keys.signed_pre_key_history:
                self._append_new_signed_pre_key_locked()
                history_mutated = True
            else:
                active = self._pre_keys.signed_pre_key_history[-1]
                age = self._now_provider() - active.generated_at
                if age >= self._rotation_options.rotation_interval:
                    self._append_new_signed_pre_key_locked()
                    history_mutated = True

            active_entry = self._pre_keys.signed_pre_key_history[-1]
            signed_pre_key_id = active_entry.id
            spk_pub = active_entry.public_key
            signature = active_entry.signature

            self._top_up_opk_pool_locked()
            pre_key_id = self._pre_keys.available_opk_ids.popleft()
            _, otpk_pub = self._pre_keys.one_time_pre_keys[pre_key_id]

            if history_mutated:
                self._persist_signed_pre_keys()
            self._persist_one_time_pre_keys()

        return PreKeyBundle(
            uhid=local_uhid,
            identity_key=bytes(self._ed25519_public_key),
            identity_key_x25519=bytes(self._identity_x25519_pub),
            pre_key_id=pre_key_id,
            pre_key=bytes(otpk_pub),
            signed_pre_key_id=signed_pre_key_id,
            signed_pre_key=bytes(spk_pub),
            signed_pre_key_signature=signature,
        )

    def _append_new_signed_pre_key_locked(self) -> None:
        """Generate a fresh SPK, append it as the new active entry, and trim
        the history to the retention budget. Caller MUST hold ``_opk_lock``.

        Mirrors the C# ``AppendNewSignedPreKeyNoLock``: the freshly-generated
        Ed25519 signature is computed under the local identity key; the
        oldest entries beyond the retention budget are dropped (and their
        private halves are released for GC — Python ``bytes`` objects are
        immutable so we cannot zero them in-place).
        """
        spk_priv_obj = X25519PrivateKey.generate()
        spk_priv = spk_priv_obj.private_bytes(
            encoding=Encoding.Raw,
            format=PrivateFormat.Raw,
            encryption_algorithm=NoEncryption(),
        )
        spk_pub = spk_priv_obj.public_key().public_bytes(
            encoding=Encoding.Raw, format=PublicFormat.Raw
        )
        signed_pre_key_id = int.from_bytes(os.urandom(4), "big") % (2**31 - 1) + 1
        signature = Ed25519SigningService.sign(self._ed25519_private_key, spk_pub)
        entry = _SignedPreKeyEntry(
            id=signed_pre_key_id,
            private_key=spk_priv,
            public_key=spk_pub,
            signature=signature,
            generated_at=self._now_provider(),
        )
        self._pre_keys.signed_pre_key_history.append(entry)

        # Prune the oldest entries beyond (1 active + retained) cap.
        max_entries = 1 + self._rotation_options.retained_history_count
        while len(self._pre_keys.signed_pre_key_history) > max_entries:
            self._pre_keys.signed_pre_key_history.pop(0)

        # Denormalise active fields for fast-path accessors.
        active = self._pre_keys.signed_pre_key_history[-1]
        self._pre_keys.signed_pre_key_id = active.id
        self._pre_keys.signed_pre_key_priv = active.private_key
        self._pre_keys.signed_pre_key_pub = active.public_key
        self._pre_keys.signed_pre_key_signature = active.signature

    async def rotate_signed_pre_key(self) -> bool:
        """Force a signed-pre-key rotation if the active SPK is older than
        :attr:`SignedPreKeyRotationOptions.rotation_interval`.

        Returns True iff a new SPK was generated and persisted. The
        history is rolled forward and trimmed to the retention budget, and
        the persisted snapshot is updated. Useful for tests and for hosts
        that want to drive rotation explicitly rather than rely on the
        next bundle generation to trigger it.
        """
        rotated = False
        async with self._opk_lock:
            should_rotate = (
                not self._pre_keys.signed_pre_key_history
                or (self._now_provider() - self._pre_keys.signed_pre_key_history[-1].generated_at)
                    >= self._rotation_options.rotation_interval
            )
            if should_rotate:
                self._append_new_signed_pre_key_locked()
                self._persist_signed_pre_keys()
                rotated = True
        if rotated:
            _LOGGER.info(
                "Rotated signed pre-key (history size now %d).",
                len(self._pre_keys.signed_pre_key_history))
        return rotated

    @property
    def active_signed_pre_key_id(self) -> int:
        """Active signed-pre-key id. Tests + observability."""
        return self._pre_keys.signed_pre_key_id

    @property
    def signed_pre_key_history_count(self) -> int:
        """Number of signed-pre-keys held — active + retained prior."""
        return len(self._pre_keys.signed_pre_key_history)

    def _find_signed_pre_key(self, spk_id: int) -> Optional[_SignedPreKeyEntry]:
        """Look up a signed-pre-key entry by id across the full retained history.

        Returns None if the id is unknown (rotated out, never generated, or
        never advertised). Iterates newest-first since the active entry is
        the most likely match in steady state.
        """
        for entry in reversed(self._pre_keys.signed_pre_key_history):
            if entry.id == spk_id:
                return entry
        return None

    def get_opk_pool_status(self) -> Tuple[int, int]:
        """Snapshot of the OPK pool for observability.

        Returns:
            (held, available) where:
              * ``held`` is the total number of OPKs whose private half
                this service holds (un-issued + issued-but-not-yet-consumed).
              * ``available`` is the number of OPKs in the pool that have
                not yet been advertised to any peer in a bundle.

        ``available`` is what gets compared against :attr:`opk_pool_size`
        on each top-up; ``held - available`` is the number of OPKs that
        have been issued at least once but not yet consumed.
        """
        return (
            len(self._pre_keys.one_time_pre_keys),
            len(self._pre_keys.available_opk_ids),
        )

    @property
    def opk_pool_size(self) -> int:
        """Target size of the OPK pool. Configured at construction time."""
        return self._opk_pool_size

    def _top_up_opk_pool_locked(self) -> None:
        """Generate fresh OPKs until ``available_opk_ids`` reaches
        :attr:`_opk_pool_size`. Caller MUST hold :attr:`_opk_lock`.

        Mirrors the C# ``TopUpOpkPoolNoLock`` logic: random non-colliding
        ids are chosen for each new OPK, retrying up to
        :data:`_MAX_OPK_ID_ALLOC_ATTEMPTS` times before raising on
        collision (statistically negligible — safety net for RNG failure).
        """
        while len(self._pre_keys.available_opk_ids) < self._opk_pool_size:
            otpk_priv_obj = X25519PrivateKey.generate()
            otpk_priv = otpk_priv_obj.private_bytes(
                encoding=Encoding.Raw,
                format=PrivateFormat.Raw,
                encryption_algorithm=NoEncryption(),
            )
            otpk_pub = otpk_priv_obj.public_key().public_bytes(
                encoding=Encoding.Raw, format=PublicFormat.Raw
            )

            # Choose a non-colliding id.
            attempts = 0
            while True:
                pre_key_id = int.from_bytes(os.urandom(4), "big") % (2**31 - 1) + 1
                if pre_key_id not in self._pre_keys.one_time_pre_keys:
                    break
                attempts += 1
                if attempts > _MAX_OPK_ID_ALLOC_ATTEMPTS:
                    raise RuntimeError(
                        "Could not allocate a non-colliding OPK id after "
                        f"{_MAX_OPK_ID_ALLOC_ATTEMPTS} attempts. Pool exhaustion or "
                        "RNG failure."
                    )

            self._pre_keys.one_time_pre_keys[pre_key_id] = (otpk_priv, otpk_pub)
            self._pre_keys.available_opk_ids.append(pre_key_id)

    async def process_pre_key_bundle(self, bundle: PreKeyBundle) -> None:
        """Establish initiator-side session via X3DH (Signal §3.3).

        After X3DH, the Signal-canonical X3DH↔Double-Ratchet integration is
        used: the initiator's X3DH ephemeral becomes its first DHs. The peer's
        signed pre-key is the initial DHr. CKs is computed lazily on first
        send (via _dh_ratchet_send_only).
        """
        if bundle is None:
            raise ValueError("bundle cannot be None")

        if not Ed25519SigningService.verify(
            bundle.identity_key, bundle.signed_pre_key, bundle.signed_pre_key_signature
        ):
            raise ValueError("Signed pre-key signature verification failed")

        if len(bundle.identity_key_x25519) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Bundle has malformed X25519 identity key "
                f"(length {len(bundle.identity_key_x25519)}, expected {_X25519_PUBLIC_KEY_SIZE})"
            )
        if len(bundle.signed_pre_key) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Bundle has malformed signed pre-key "
                f"(length {len(bundle.signed_pre_key)}, expected {_X25519_PUBLIC_KEY_SIZE})"
            )
        if len(bundle.pre_key) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Bundle has malformed one-time pre-key "
                f"(length {len(bundle.pre_key)}, expected {_X25519_PUBLIC_KEY_SIZE})"
            )

        # Fresh ephemeral X25519 keypair, generated per-session per Signal §3.3.
        ek_priv_obj = X25519PrivateKey.generate()
        ek_priv = ek_priv_obj.private_bytes(
            encoding=Encoding.Raw,
            format=PrivateFormat.Raw,
            encryption_algorithm=NoEncryption(),
        )
        ek_pub = ek_priv_obj.public_key().public_bytes(
            encoding=Encoding.Raw, format=PublicFormat.Raw
        )

        # X3DH 4-DH key agreement (initiator side).
        dh1 = self._x25519_agree(self._identity_x25519_priv, bundle.signed_pre_key)
        dh2 = self._x25519_agree(ek_priv, bundle.identity_key_x25519)
        dh3 = self._x25519_agree(ek_priv, bundle.signed_pre_key)
        dh4 = self._x25519_agree(ek_priv, bundle.pre_key)

        shared_secret = dh1 + dh2 + dh3 + dh4
        root_key = self._hkdf(shared_secret, _HKDF_ROOT_INFO)

        # Adopt X3DH ephemeral as initial DHs; peer SPK as initial DHr.
        # CKs / CKr left None — derived lazily on first send / first DH-ratchet.
        session = SignalSession(
            root_key=root_key,
            send_chain_key=None,
            recv_chain_key=None,
            my_ephemeral_priv=bytes(ek_priv),
            my_ephemeral_pub=bytes(ek_pub),
            remote_ephemeral_pub=bytes(bundle.signed_pre_key),
            pending_pre_key_message=True,
            initiator_identity_key_x25519=bytes(self._identity_x25519_pub),
            used_signed_pre_key_id=bundle.signed_pre_key_id,
            used_one_time_pre_key_id=bundle.pre_key_id,
        )
        self._sessions[bundle.uhid] = session
        self._persist_session(bundle.uhid, session)

    async def _establish_responder_session(
        self, peer_uhid: str, payload: EncryptedPayload, initiator_ratchet_pub: bytes
    ) -> None:
        """Mirror the initiator's 4 X3DH DHs to derive the same root key,
        then prepare for a DH-ratchet step on the same call's decrypt path.

        The signed pre-key (private + public) is adopted as the responder's
        initial DHs; a fresh keypair is generated when the DH-ratchet step
        rotates it. ``remote_ephemeral_pub`` is left None to force a
        DH-ratchet step on the upcoming decrypt.

        Concurrency: the OPK look-up + consume is performed under
        :attr:`_opk_lock` so two concurrent initiators that happened to
        race on the SAME OPK id cannot both see "not yet consumed" and
        proceed — exactly one wins, the loser raises with the standard
        "already consumed" error.
        """
        ik = payload.initiator_identity_key_x25519
        ek = initiator_ratchet_pub
        if ik is None or len(ik) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Initiator IK_X25519 has wrong size ({len(ik) if ik else 0}, "
                f"expected {_X25519_PUBLIC_KEY_SIZE})"
            )
        if ek is None or len(ek) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Initiator ratchet pub has wrong size ({len(ek) if ek else 0}, "
                f"expected {_X25519_PUBLIC_KEY_SIZE})"
            )

        # Atomically look up + consume the OPK private half. SPK is looked
        # up across the FULL retained history so that messages signed under
        # a recently-rotated SPK still complete X3DH during the rotation
        # window.
        async with self._opk_lock:
            spk_entry = self._find_signed_pre_key(payload.used_signed_pre_key_id)
            if spk_entry is None:
                raise ValueError(
                    f"PreKey message references signed pre-key id "
                    f"{payload.used_signed_pre_key_id} which is not held by this node "
                    "(rotated out or never generated)."
                )
            if payload.used_one_time_pre_key_id not in self._pre_keys.one_time_pre_keys:
                raise ValueError(
                    f"PreKey message references one-time pre-key id "
                    f"{payload.used_one_time_pre_key_id} which is not held "
                    "(already consumed, or never generated)."
                )
            otpk_priv, _ = self._pre_keys.one_time_pre_keys[payload.used_one_time_pre_key_id]

            # Consume the OPK BEFORE dropping the lock so a concurrent
            # initiator using the same id sees "already consumed".
            del self._pre_keys.one_time_pre_keys[payload.used_one_time_pre_key_id]
            # If the consumed id was still in the available deque (an
            # initiator that received the bundle before we issued any
            # other), drop it from there too — non-fatal if already gone.
            try:
                self._pre_keys.available_opk_ids.remove(
                    payload.used_one_time_pre_key_id
                )
            except ValueError:
                pass

            spk_priv = spk_entry.private_key
            spk_pub = spk_entry.public_key
            # Persist the OPK consume — the in-memory delete above won't
            # survive a restart by itself.
            self._consume_one_time_pre_key_persistent(payload.used_one_time_pre_key_id)

        # Crypto outside the lock — only state mutation is locked.
        # Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
        dh1 = self._x25519_agree(spk_priv, ik)
        dh2 = self._x25519_agree(self._identity_x25519_priv, ek)
        dh3 = self._x25519_agree(spk_priv, ek)
        dh4 = self._x25519_agree(otpk_priv, ek)

        shared_secret = dh1 + dh2 + dh3 + dh4
        root_key = self._hkdf(shared_secret, _HKDF_ROOT_INFO)

        # Adopt SPK as initial DHs. The DH-ratchet step that follows on the
        # same decrypt() call will rotate it to a fresh keypair.
        session = SignalSession(
            root_key=root_key,
            send_chain_key=None,
            recv_chain_key=None,
            my_ephemeral_priv=bytes(spk_priv),
            my_ephemeral_pub=bytes(spk_pub),
            remote_ephemeral_pub=None,  # forces DH-ratchet on first decrypt
            pending_pre_key_message=False,
        )
        self._sessions[peer_uhid] = session
        self._persist_session(peer_uhid, session)

    def _dh_ratchet_receive(self, session: SignalSession, new_remote_ephemeral_pub: bytes) -> None:
        """Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
        derives a new receiving chain via KDF_RK(RK, DH(DHs, DHr)), generates a
        fresh DHs, and derives a new sending chain via KDF_RK(RK, DH(newDHs, DHr)).
        """
        # Save send-counter as PN so the peer can compute skipped keys
        # across the ratchet boundary on subsequent decrypts.
        session.previous_chain_count = session.send_counter
        session.send_counter = 0
        session.recv_counter = 0
        session.remote_ephemeral_pub = bytes(new_remote_ephemeral_pub)

        # Step 1: derive new receiving chain from current DHs · new DHr.
        dh1 = self._x25519_agree(session.my_ephemeral_priv, session.remote_ephemeral_pub)
        new_root, new_ckr = self._kdf_rk(session.root_key, dh1)
        session.root_key = new_root
        session.recv_chain_key = new_ckr

        # Step 2: rotate DHs to a fresh keypair, derive new sending chain
        # from new DHs · new DHr.
        new_priv_obj = X25519PrivateKey.generate()
        new_priv = new_priv_obj.private_bytes(
            encoding=Encoding.Raw,
            format=PrivateFormat.Raw,
            encryption_algorithm=NoEncryption(),
        )
        new_pub = new_priv_obj.public_key().public_bytes(
            encoding=Encoding.Raw, format=PublicFormat.Raw
        )
        session.my_ephemeral_priv = new_priv
        session.my_ephemeral_pub = new_pub

        dh2 = self._x25519_agree(session.my_ephemeral_priv, session.remote_ephemeral_pub)
        new_root, new_cks = self._kdf_rk(session.root_key, dh2)
        session.root_key = new_root
        session.send_chain_key = new_cks

    def _dh_ratchet_send_only(self, session: SignalSession, remote_pub: bytes) -> None:
        """Lazy half-ratchet for the very first send on a freshly-established
        initiator session. The initiator's DHs and DHr are already set
        (X3DH placed them); we just need to derive the sending chain. We do
        NOT rotate DHs here — only on a true DH-ratchet (i.e. on receive).
        """
        dh = self._x25519_agree(session.my_ephemeral_priv, remote_pub)
        new_root, new_cks = self._kdf_rk(session.root_key, dh)
        session.root_key = new_root
        session.send_chain_key = new_cks

    def _skip_message_keys(self, session: SignalSession, until: int) -> None:
        """Saves any unread message keys on the current receive chain up to
        the given counter, so they can be consumed if those messages
        eventually arrive after a DH-ratchet step. Bounded by MAX_SKIPPED_KEYS.
        """
        if session.recv_chain_key is None or session.remote_ephemeral_pub is None:
            return  # no chain to skip on
        if until <= session.recv_counter:
            return
        if until - session.recv_counter > MAX_SKIPPED_KEYS:
            raise ValueError(
                f"Skipped-key request exceeds maximum ({MAX_SKIPPED_KEYS}). "
                "Session must be re-established."
            )

        while session.recv_counter < until:
            skip_key = self._ratchet_recv_chain(session)
            session.skipped_message_keys[
                self._skipped_key(session.remote_ephemeral_pub, session.recv_counter)
            ] = skip_key
            session.recv_counter += 1

    async def sign_data(self, data: bytes) -> bytes:
        if data is None:
            raise ValueError("data cannot be None")
        return Ed25519SigningService.sign(self._ed25519_private_key, data)

    def verify_signature(self, public_key: bytes, data: bytes, signature: bytes) -> bool:
        return Ed25519SigningService.verify(public_key, data, signature)

    def get_public_key(self) -> bytes:
        """Get a copy of this node's Ed25519 public key."""
        return bytes(self._ed25519_public_key)

    def get_x25519_public_key(self) -> bytes:
        """Get a copy of this node's X25519 ECDH public key."""
        return bytes(self._identity_x25519_pub)

    @staticmethod
    def _x25519_agree(local_priv_bytes: bytes, remote_pub_bytes: bytes) -> bytes:
        """Compute X25519 shared secret between local private and remote public.

        RFC 7748 §6.1: detect the all-zero output (small-subgroup attack via a
        low-order remote public key). The cryptography library's `exchange()`
        does not raise on that condition by itself — we check defensively.
        """
        priv = X25519PrivateKey.from_private_bytes(local_priv_bytes)
        pub = X25519PublicKey.from_public_bytes(remote_pub_bytes)
        shared = priv.exchange(pub)
        if shared == bytes(_X25519_PUBLIC_KEY_SIZE):
            raise ValueError(
                "X25519 produced an all-zero shared secret (low-order point)"
            )
        return shared

    @staticmethod
    def _hkdf(input_key_material: bytes, info: bytes) -> bytes:
        """HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey."""
        kdf = HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=None,
            info=info,
            backend=default_backend(),
        )
        return kdf.derive(input_key_material)

    @staticmethod
    def _kdf_rk(root_key: bytes, dh_output: bytes) -> Tuple[bytes, bytes]:
        """KDF_RK per Signal §5.2: derives a new root key + new chain key
        from the current root key and a fresh DH output. HKDF-SHA256 over
        64 bytes; first 32 = new root, second 32 = new chain key.
        """
        kdf = HKDF(
            algorithm=hashes.SHA256(),
            length=64,
            salt=root_key,
            info=_HKDF_RATCHET_INFO,
            backend=default_backend(),
        )
        derived = kdf.derive(dh_output)
        return derived[:32], derived[32:]

    def _ratchet_send_chain(self, session: SignalSession) -> bytearray:
        new_chain, message_key = self._ratchet(session.send_chain_key)
        session.send_chain_key = new_chain
        return message_key

    def _ratchet_recv_chain(self, session: SignalSession) -> bytearray:
        new_chain, message_key = self._ratchet(session.recv_chain_key)
        session.recv_chain_key = new_chain
        return message_key

    @staticmethod
    def _ratchet(chain_key: bytes) -> Tuple[bytes, bytearray]:
        """Single Double-Ratchet step (Signal §5.1).

        message_key   = HMAC-SHA256(chain_key, 0x01)
        new_chain_key = HMAC-SHA256(chain_key, 0x02)

        ``message_key`` is returned as ``bytearray`` (not ``bytes``) so the
        caller can zero it after use via ``_zero_memory``. Python's
        ``hmac.HMAC.finalize()`` returns an immutable ``bytes`` object that
        cannot be scrubbed in place; the ``bytearray`` copy holds the same
        bits but allows overwrite.
        """
        h1 = hmac.HMAC(chain_key, hashes.SHA256(), backend=default_backend())
        h1.update(b"\x01")
        message_key = bytearray(h1.finalize())  # mutable — caller zeroes after use

        h2 = hmac.HMAC(chain_key, hashes.SHA256(), backend=default_backend())
        h2.update(b"\x02")
        new_chain = h2.finalize()
        return new_chain, message_key

    @staticmethod
    def _skipped_key(dhr_pub: bytes, counter: int) -> str:
        """Compose the skipped-message-keys cache key.

        Mirrors the C# format ``Convert.ToHexString(dhrPub):counter`` —
        uppercase hex of the DHr public, colon, integer counter. The
        DHr binding is essential: out-of-order messages from a previous
        chain (different DHr) need their own per-chain key set.
        """
        return f"{dhr_pub.hex().upper()}:{counter}"

    @staticmethod
    def _zero_memory(data) -> None:
        """Best-effort zeroing.

        Overwrites every byte of a ``bytearray`` or writable ``memoryview``
        with zero. Per-message AES-GCM keys (returned by ``_ratchet`` as
        ``bytearray``) are zeroed after use so they do not persist in heap
        memory beyond the encrypt/decrypt call.

        Python ``bytes`` objects are immutable and cannot be scrubbed in
        place. If ``data`` is ``bytes``, this is a no-op — callers should
        avoid passing ``bytes`` for sensitive material.
        """
        if isinstance(data, bytearray):
            for i in range(len(data)):
                data[i] = 0
        elif isinstance(data, memoryview):
            if not data.readonly:
                for i in range(len(data)):
                    data[i] = 0
        else:
            return
