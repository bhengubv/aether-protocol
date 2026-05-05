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

import hmac as stdlib_hmac
import os
from dataclasses import dataclass, field
from typing import Dict, Optional, Tuple
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

from aether.security.ed25519_service import Ed25519SigningService
from aether.constants import MAX_SKIPPED_KEYS, AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE


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


class _PreKeyState:
    """Responder-side pre-key state.

    Holds the private halves of the signed pre-key and one-time pre-keys so
    we can run our side of X3DH when an initiator's PreKey message arrives.
    """

    def __init__(self) -> None:
        self.signed_pre_key_id: int = 0
        self.signed_pre_key_priv: bytes = b""
        self.signed_pre_key_pub: bytes = b""
        self.signed_pre_key_signature: bytes = b""
        # int -> (priv, pub). Each entry is consumed (zeroed and removed) on first use.
        self.one_time_pre_keys: Dict[int, Tuple[bytes, bytes]] = {}


class SignalProtocolService:
    """Signal Protocol implementation: X3DH + full Double Ratchet (Signal §5)."""

    def __init__(self) -> None:
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

        self._initialize_identity_keys()

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

    def set_local_uhid(self, local_uhid: str) -> None:
        """Set the local node's UHID. Required before any encrypt() call."""
        if not local_uhid:
            raise ValueError("local_uhid cannot be empty")
        self._local_uhid = local_uhid

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
                return payload

            return EncryptedPayload(
                ciphertext=ciphertext,
                nonce=nonce,
                message_type=MESSAGE_TYPE_NORMAL,
                sender_uhid=self._local_uhid,
                counter=counter,
                sender_ephemeral_key_x25519=ratchet_pub,
                previous_chain_count=session.previous_chain_count,
            )
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
            self._establish_responder_session(peer_uhid, payload, sender_ratchet_pub)

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
            return plaintext
        except Exception as e:
            raise ValueError(f"Decryption failed: {e}")
        finally:
            self._zero_memory(message_key)

    async def generate_pre_key_bundle(self, local_uhid: str) -> PreKeyBundle:
        """Generate a pre-key bundle. Retains the SPK + OPK private halves
        for responder-side X3DH on this node.
        """
        if not local_uhid:
            raise ValueError("local_uhid cannot be empty")
        self._local_uhid = local_uhid

        # One-time pre-key.
        otpk_priv_obj = X25519PrivateKey.generate()
        otpk_priv = otpk_priv_obj.private_bytes(
            encoding=Encoding.Raw,
            format=PrivateFormat.Raw,
            encryption_algorithm=NoEncryption(),
        )
        otpk_pub = otpk_priv_obj.public_key().public_bytes(
            encoding=Encoding.Raw, format=PublicFormat.Raw
        )
        pre_key_id = int.from_bytes(os.urandom(4), "big") % (2**31 - 1) + 1
        self._pre_keys.one_time_pre_keys[pre_key_id] = (otpk_priv, otpk_pub)

        # Signed pre-key.
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

        # Signature is over the X25519 SPK public, signed by Ed25519 identity.
        signature = Ed25519SigningService.sign(
            self._ed25519_private_key, spk_pub
        )
        self._pre_keys.signed_pre_key_id = signed_pre_key_id
        self._pre_keys.signed_pre_key_priv = spk_priv
        self._pre_keys.signed_pre_key_pub = spk_pub
        self._pre_keys.signed_pre_key_signature = signature

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

    def _establish_responder_session(
        self, peer_uhid: str, payload: EncryptedPayload, initiator_ratchet_pub: bytes
    ) -> None:
        """Mirror the initiator's 4 X3DH DHs to derive the same root key,
        then prepare for a DH-ratchet step on the same call's decrypt path.

        The signed pre-key (private + public) is adopted as the responder's
        initial DHs; a fresh keypair is generated when the DH-ratchet step
        rotates it. ``remote_ephemeral_pub`` is left None to force a
        DH-ratchet step on the upcoming decrypt. The one-time pre-key is
        consumed.
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
        if (self._pre_keys.signed_pre_key_id != payload.used_signed_pre_key_id
                or not self._pre_keys.signed_pre_key_priv):
            raise ValueError(
                f"PreKey message references signed pre-key id "
                f"{payload.used_signed_pre_key_id} which is not held by this node."
            )
        if payload.used_one_time_pre_key_id not in self._pre_keys.one_time_pre_keys:
            raise ValueError(
                f"PreKey message references one-time pre-key id "
                f"{payload.used_one_time_pre_key_id} which is not held "
                "(already consumed, or never generated)."
            )
        otpk_priv, _ = self._pre_keys.one_time_pre_keys[payload.used_one_time_pre_key_id]

        # Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
        dh1 = self._x25519_agree(self._pre_keys.signed_pre_key_priv, ik)
        dh2 = self._x25519_agree(self._identity_x25519_priv, ek)
        dh3 = self._x25519_agree(self._pre_keys.signed_pre_key_priv, ek)
        dh4 = self._x25519_agree(otpk_priv, ek)

        shared_secret = dh1 + dh2 + dh3 + dh4
        root_key = self._hkdf(shared_secret, _HKDF_ROOT_INFO)

        # Adopt SPK as initial DHs. The DH-ratchet step that follows on the
        # same decrypt() call will rotate it to a fresh keypair.
        self._sessions[peer_uhid] = SignalSession(
            root_key=root_key,
            send_chain_key=None,
            recv_chain_key=None,
            my_ephemeral_priv=bytes(self._pre_keys.signed_pre_key_priv),
            my_ephemeral_pub=bytes(self._pre_keys.signed_pre_key_pub),
            remote_ephemeral_pub=None,  # forces DH-ratchet on first decrypt
            pending_pre_key_message=False,
        )

        # Consume one-time pre-key — never reuse (replay protection).
        del self._pre_keys.one_time_pre_keys[payload.used_one_time_pre_key_id]

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

    def _ratchet_send_chain(self, session: SignalSession) -> bytes:
        new_chain, message_key = self._ratchet(session.send_chain_key)
        session.send_chain_key = new_chain
        return message_key

    def _ratchet_recv_chain(self, session: SignalSession) -> bytes:
        new_chain, message_key = self._ratchet(session.recv_chain_key)
        session.recv_chain_key = new_chain
        return message_key

    @staticmethod
    def _ratchet(chain_key: bytes) -> Tuple[bytes, bytes]:
        """Single Double-Ratchet step (Signal §5.1).

        message_key   = HMAC-SHA256(chain_key, 0x01)
        new_chain_key = HMAC-SHA256(chain_key, 0x02)
        """
        h1 = hmac.HMAC(chain_key, hashes.SHA256(), backend=default_backend())
        h1.update(b"\x01")
        message_key = h1.finalize()

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

        Python `bytes` objects are immutable and cannot be scrubbed in place.
        Callers that care about memory hygiene should hold sensitive material
        as `bytearray` and pass that here. For `bytes`, this is a no-op (the
        GC will eventually reclaim, but contents may persist until then).
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
