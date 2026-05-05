"""Signal Protocol implementation for end-to-end encryption in Aether mesh.

Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
  - DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
  - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
  - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
  - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)

Root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
Symmetric ratchet: HMAC-SHA256, single-byte domain separation
  (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
Identity signing: Ed25519 via Ed25519SigningService.
"""

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
# chain (and vice versa).
#
# These MUST match the C# reference exactly — any drift breaks cross-language
# interop (verified by fixtures/signal/expected/x3dh_basic.json).
_HKDF_ROOT_INFO = b"aether-x3dh-root-v1"
_HKDF_CHAIN_INITIATOR_SEND_INFO = b"aether-chain-initiator-send-v1"
_HKDF_CHAIN_INITIATOR_RECV_INFO = b"aether-chain-initiator-recv-v1"

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

    When `message_type == 1` (PreKey message — first message from initiator
    before responder has established a session), the four `initiator_*`
    fields carry the data the responder needs to run X3DH on its side.
    On normal messages (`message_type == 0`), those fields are None/0.
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


@dataclass
class SignalSession:
    """State of a Signal Protocol session with a single peer.

    On the initiator side, `pending_pre_key_message` is True until the first
    outbound message is sent. While True, the next encrypt() emits a PreKey
    message carrying the four `initiator_*` fields below.
    """

    root_key: bytes = b""
    send_chain_key: bytes = b""
    recv_chain_key: bytes = b""
    send_counter: int = 0
    recv_counter: int = 0
    skipped_message_keys: Dict[int, bytes] = field(default_factory=dict)

    pending_pre_key_message: bool = False
    initiator_identity_key_x25519: bytes = b""
    initiator_ephemeral_key_x25519: bytes = b""
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
    """Signal Protocol implementation: X3DH + Double-Ratchet."""

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

        message_key = self._ratchet_send_chain(session)

        nonce = os.urandom(AES_GCM_NONCE_SIZE)
        cipher = AESGCM(message_key)
        ciphertext = cipher.encrypt(nonce, plaintext, None)

        counter = session.send_counter
        session.send_counter += 1

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
                initiator_ephemeral_key_x25519=bytes(session.initiator_ephemeral_key_x25519),
                used_signed_pre_key_id=session.used_signed_pre_key_id,
                used_one_time_pre_key_id=session.used_one_time_pre_key_id,
            )
            session.pending_pre_key_message = False
            self._zero_memory(message_key)
            return payload

        self._zero_memory(message_key)
        return EncryptedPayload(
            ciphertext=ciphertext,
            nonce=nonce,
            message_type=MESSAGE_TYPE_NORMAL,
            sender_uhid=self._local_uhid,
            counter=counter,
        )

    async def decrypt(self, peer_uhid: str, payload: EncryptedPayload) -> bytes:
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if payload is None:
            raise ValueError("payload cannot be None")

        # PreKey message? Establish (or replace) the responder-side session
        # before attempting decryption.
        if payload.message_type == MESSAGE_TYPE_PRE_KEY:
            if not payload.initiator_identity_key_x25519 or not payload.initiator_ephemeral_key_x25519:
                raise ValueError(
                    "PreKey message missing initiator key material "
                    "(initiator_identity_key_x25519 / initiator_ephemeral_key_x25519)."
                )
            self._establish_responder_session(peer_uhid, payload)

        session = self._sessions.get(peer_uhid)
        if session is None:
            raise ValueError(f"No session established with peer {peer_uhid}")

        if len(payload.ciphertext) < AES_GCM_TAG_SIZE:
            raise ValueError("Ciphertext too short")

        # Skipped key cache?
        if payload.counter in session.skipped_message_keys:
            message_key = session.skipped_message_keys.pop(payload.counter)
        else:
            gap = payload.counter - session.recv_counter
            if gap > MAX_SKIPPED_KEYS:
                raise ValueError(
                    f"Message counter gap ({gap}) exceeds maximum ({MAX_SKIPPED_KEYS}). "
                    "Session must be re-established."
                )
            while session.recv_counter < payload.counter:
                skip_key = self._ratchet_recv_chain(session)
                session.skipped_message_keys[session.recv_counter] = skip_key
                session.recv_counter += 1
            message_key = self._ratchet_recv_chain(session)
            session.recv_counter += 1

        try:
            cipher = AESGCM(message_key)
            plaintext = cipher.decrypt(payload.nonce, payload.ciphertext, None)
            self._zero_memory(message_key)
            return plaintext
        except Exception as e:
            self._zero_memory(message_key)
            raise ValueError(f"Decryption failed: {e}")

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
        """Establish initiator-side session via X3DH (Signal §3.3)."""
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

        try:
            # X3DH 4-DH key agreement (initiator side).
            dh1 = self._x25519_agree(self._identity_x25519_priv, bundle.signed_pre_key)
            dh2 = self._x25519_agree(ek_priv, bundle.identity_key_x25519)
            dh3 = self._x25519_agree(ek_priv, bundle.signed_pre_key)
            dh4 = self._x25519_agree(ek_priv, bundle.pre_key)

            shared_secret = dh1 + dh2 + dh3 + dh4
            root_key = self._hkdf(shared_secret, _HKDF_ROOT_INFO)
            send_chain = self._hkdf(root_key, _HKDF_CHAIN_INITIATOR_SEND_INFO)
            recv_chain = self._hkdf(root_key, _HKDF_CHAIN_INITIATOR_RECV_INFO)

            session = SignalSession(
                root_key=root_key,
                send_chain_key=send_chain,
                recv_chain_key=recv_chain,
                pending_pre_key_message=True,
                initiator_identity_key_x25519=bytes(self._identity_x25519_pub),
                initiator_ephemeral_key_x25519=bytes(ek_pub),
                used_signed_pre_key_id=bundle.signed_pre_key_id,
                used_one_time_pre_key_id=bundle.pre_key_id,
            )
            self._sessions[bundle.uhid] = session
        finally:
            self._zero_memory(ek_priv)

    def _establish_responder_session(self, peer_uhid: str, payload: EncryptedPayload) -> None:
        """Mirror the initiator's 4 X3DH DHs to derive the same root key.

        Chain-key info strings are SWAPPED relative to the initiator so the
        initiator's send chain matches the responder's recv chain. Consumes
        and zeros the one-time pre-key.
        """
        ik = payload.initiator_identity_key_x25519
        ek = payload.initiator_ephemeral_key_x25519
        if ik is None or len(ik) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Initiator IK_X25519 has wrong size ({len(ik) if ik else 0}, "
                f"expected {_X25519_PUBLIC_KEY_SIZE})"
            )
        if ek is None or len(ek) != _X25519_PUBLIC_KEY_SIZE:
            raise ValueError(
                f"Initiator EK_X25519 has wrong size ({len(ek) if ek else 0}, "
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
        # SWAPPED.
        recv_chain = self._hkdf(root_key, _HKDF_CHAIN_INITIATOR_SEND_INFO)
        send_chain = self._hkdf(root_key, _HKDF_CHAIN_INITIATOR_RECV_INFO)

        self._sessions[peer_uhid] = SignalSession(
            root_key=root_key,
            send_chain_key=send_chain,
            recv_chain_key=recv_chain,
        )

        # Consume one-time pre-key — never reuse (replay protection).
        del self._pre_keys.one_time_pre_keys[payload.used_one_time_pre_key_id]

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
