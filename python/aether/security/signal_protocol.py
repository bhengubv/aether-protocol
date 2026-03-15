"""Signal Protocol implementation for end-to-end encryption in Aether mesh."""

import os
import hashlib
import struct
from dataclasses import dataclass
from typing import Dict, Optional, Tuple
from cryptography.hazmat.primitives import hashes, hmac
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.backends import default_backend

from aether.security.ed25519_service import Ed25519SigningService
from aether.constants import MAX_SKIPPED_KEYS, AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE


@dataclass
class PreKeyBundle:
    """
    Pre-key bundle for asynchronous session establishment.

    Allows a sender to establish an encrypted session without the recipient
    being online.
    """

    uhid: str
    identity_key: bytes  # Ed25519 public key (32 bytes)
    pre_key_id: int
    pre_key: bytes  # ECDH P-256 public key (65 bytes uncompressed)
    signed_pre_key_id: int
    signed_pre_key: bytes  # ECDH P-256 public key (65 bytes uncompressed)
    signed_pre_key_signature: bytes  # Ed25519 signature (64 bytes)


@dataclass
class EncryptedPayload:
    """
    Encrypted payload with encryption metadata.
    """

    ciphertext: bytes  # Ciphertext + 16-byte GCM tag
    nonce: bytes  # 12-byte AES-GCM nonce
    message_type: int  # 1 = PreKey, 2 = Regular
    sender_uhid: str
    counter: int


class SignalSession:
    """Tracks the state of a Signal Protocol session with a single peer."""

    def __init__(self) -> None:
        self.root_key: bytes = b""
        self.send_chain_key: bytes = b""
        self.recv_chain_key: bytes = b""
        self.send_counter: int = 0
        self.recv_counter: int = 0
        self.remote_public_key: bytes = b""
        self.skipped_message_keys: Dict[int, bytes] = {}


class SignalProtocolService:
    """
    Signal Protocol implementation providing end-to-end encryption for Aether mesh.

    Key agreement: X3DH with ECDH P-256.
    Key derivation: HKDF-SHA256 with unique info strings per derivation context.
    Encryption: AES-256-GCM with 12-byte nonce and 16-byte authentication tag.
    Signing: Ed25519 via Ed25519SigningService.
    """

    def __init__(self) -> None:
        """Initialize the Signal Protocol service."""
        self._sessions: Dict[str, SignalSession] = {}
        self._identity_private_key: bytes = b""
        self._identity_public_key: bytes = b""
        self._ed25519_private_key: bytes = b""
        self._ed25519_public_key: bytes = b""
        self._initialize_identity_keys()

    def _initialize_identity_keys(self) -> None:
        """Initialize Ed25519 and ECDH identity key pairs."""
        # Generate Ed25519 key pair for signing
        self._ed25519_private_key, self._ed25519_public_key = (
            Ed25519SigningService.generate_keypair()
        )

        # Generate ECDH P-256 key pair for key agreement
        private_key = ec.generate_private_key(ec.SECP256R1(), default_backend())
        self._identity_private_key = private_key.private_numbers().private_value.to_bytes(
            32, "big"
        )
        public_numbers = private_key.public_key().public_numbers()
        self._identity_public_key = self._export_ec_public_key(public_numbers)

    def has_session(self, peer_uhid: str) -> bool:
        """Check if a session exists with a peer."""
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        return peer_uhid in self._sessions

    async def encrypt(self, peer_uhid: str, plaintext: bytes) -> EncryptedPayload:
        """
        Encrypt data for a peer using the established session.

        Args:
            peer_uhid: The peer's UHID.
            plaintext: The data to encrypt.

        Returns:
            EncryptedPayload with ciphertext, nonce, and metadata.

        Raises:
            ValueError: If no session exists with the peer.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if plaintext is None:
            raise ValueError("plaintext cannot be None")

        session = self._sessions.get(peer_uhid)
        if session is None:
            raise ValueError(f"No session established with peer {peer_uhid}")

        # Ratchet the sending chain to derive a message key
        message_key = self._ratchet_send_chain(session)

        # Encrypt with AES-256-GCM
        nonce = os.urandom(AES_GCM_NONCE_SIZE)
        cipher = AESGCM(message_key)
        ciphertext = cipher.encrypt(nonce, plaintext, None)

        counter = session.send_counter
        session.send_counter += 1

        # Zero the message key
        self._zero_memory(message_key)

        return EncryptedPayload(
            ciphertext=ciphertext,
            nonce=nonce,
            message_type=2,  # Regular message
            sender_uhid=peer_uhid,
            counter=counter,
        )

    async def decrypt(self, peer_uhid: str, payload: EncryptedPayload) -> bytes:
        """
        Decrypt data from a peer using the established session.

        Args:
            peer_uhid: The peer's UHID.
            payload: The encrypted payload.

        Returns:
            The decrypted plaintext.

        Raises:
            ValueError: If no session exists or decryption fails.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if payload is None:
            raise ValueError("payload cannot be None")

        session = self._sessions.get(peer_uhid)
        if session is None:
            raise ValueError(f"No session established with peer {peer_uhid}")

        # Check for skipped messages
        if payload.counter in session.skipped_message_keys:
            message_key = session.skipped_message_keys.pop(payload.counter)
        else:
            # Check for excessive counter gap
            gap = payload.counter - session.recv_counter
            if gap > MAX_SKIPPED_KEYS:
                raise ValueError(
                    f"Message counter gap ({gap}) exceeds maximum ({MAX_SKIPPED_KEYS}). "
                    "Session must be re-established."
                )

            # Catch up by deriving skipped keys
            while session.recv_counter < payload.counter:
                skip_key = self._ratchet_recv_chain(session)
                session.skipped_message_keys[session.recv_counter] = skip_key
                session.recv_counter += 1

            # Derive the actual message key
            message_key = self._ratchet_recv_chain(session)
            session.recv_counter += 1

        # Decrypt with AES-256-GCM
        if len(payload.ciphertext) < AES_GCM_TAG_SIZE:
            raise ValueError("Ciphertext too short")

        try:
            cipher = AESGCM(message_key)
            plaintext = cipher.decrypt(payload.nonce, payload.ciphertext, None)
            self._zero_memory(message_key)
            return plaintext
        except Exception as e:
            self._zero_memory(message_key)
            raise ValueError(f"Decryption failed: {e}")

    async def generate_pre_key_bundle(self, local_uhid: str) -> PreKeyBundle:
        """
        Generate a pre-key bundle for publishing.

        Args:
            local_uhid: This node's UHID.

        Returns:
            A PreKeyBundle ready for distribution.
        """
        if not local_uhid:
            raise ValueError("local_uhid cannot be empty")

        # Generate one-time pre-key (ECDH P-256)
        pre_key_private = ec.generate_private_key(ec.SECP256R1(), default_backend())
        pre_key_public = self._export_ec_public_key(
            pre_key_private.public_key().public_numbers()
        )
        pre_key_id = int.from_bytes(os.urandom(4), "big") % (2**31 - 1) + 1

        # Generate signed pre-key (ECDH P-256)
        signed_pre_key_private = ec.generate_private_key(ec.SECP256R1(), default_backend())
        signed_pre_key_public = self._export_ec_public_key(
            signed_pre_key_private.public_key().public_numbers()
        )
        signed_pre_key_id = int.from_bytes(os.urandom(4), "big") % (2**31 - 1) + 1

        # Sign the signed pre-key with our Ed25519 identity key
        signature = Ed25519SigningService.sign(
            self._ed25519_private_key, signed_pre_key_public
        )

        return PreKeyBundle(
            uhid=local_uhid,
            identity_key=bytes(self._ed25519_public_key),
            pre_key_id=pre_key_id,
            pre_key=pre_key_public,
            signed_pre_key_id=signed_pre_key_id,
            signed_pre_key=signed_pre_key_public,
            signed_pre_key_signature=signature,
        )

    async def process_pre_key_bundle(self, bundle: PreKeyBundle) -> None:
        """
        Process a pre-key bundle from a peer and establish a session.

        Args:
            bundle: The peer's PreKeyBundle.

        Raises:
            ValueError: If the bundle signature is invalid.
        """
        if bundle is None:
            raise ValueError("bundle cannot be None")

        # Verify the signed pre-key signature
        if not Ed25519SigningService.verify(
            bundle.identity_key, bundle.signed_pre_key, bundle.signed_pre_key_signature
        ):
            raise ValueError("Signed pre-key signature verification failed")

        # Perform X3DH key agreement
        shared_secret = self._perform_x3dh(
            bundle.signed_pre_key, bundle.pre_key
        )

        # Derive keys using HKDF
        root_key = self._derive_key(shared_secret, b"aether-root-v1")
        send_chain_key = self._derive_key(root_key, b"aether-chain-send-v1")
        recv_chain_key = self._derive_key(root_key, b"aether-chain-recv-v1")

        # Zero the shared secret
        self._zero_memory(shared_secret)

        session = SignalSession()
        session.root_key = root_key
        session.send_chain_key = send_chain_key
        session.recv_chain_key = recv_chain_key
        session.remote_public_key = bytes(bundle.identity_key)

        self._sessions[bundle.uhid] = session

    async def sign_data(self, data: bytes) -> bytes:
        """
        Sign data using this node's Ed25519 identity key.

        Args:
            data: The data to sign.

        Returns:
            64-byte Ed25519 signature.
        """
        if data is None:
            raise ValueError("data cannot be None")
        return Ed25519SigningService.sign(self._ed25519_private_key, data)

    def verify_signature(self, public_key: bytes, data: bytes, signature: bytes) -> bool:
        """
        Verify an Ed25519 signature.

        Args:
            public_key: 32-byte Ed25519 public key.
            data: The signed data.
            signature: 64-byte signature.

        Returns:
            True if valid, False otherwise.
        """
        return Ed25519SigningService.verify(public_key, data, signature)

    def get_public_key(self) -> bytes:
        """Get a copy of this node's Ed25519 public key."""
        return bytes(self._ed25519_public_key)

    def _perform_x3dh(self, remote_signed_pre_key: bytes, remote_pre_key: bytes) -> bytes:
        """
        Perform X3DH key agreement.

        Uses our identity key against the remote's signed pre-key and one-time pre-key.
        """
        # Import our identity private key
        local_private_value = int.from_bytes(self._identity_private_key, "big")
        local_private_key = ec.derive_private_key(
            local_private_value, ec.SECP256R1(), default_backend()
        )

        # Import remote keys
        remote_signed_pk = self._import_ec_public_key(remote_signed_pre_key)
        remote_pk = self._import_ec_public_key(remote_pre_key)

        # DH1: our identity <-> their signed pre-key
        dh1 = local_private_key.exchange(ec.ECDH(), remote_signed_pk)

        # DH2: our identity <-> their one-time pre-key
        dh2 = local_private_key.exchange(ec.ECDH(), remote_pk)

        # Concatenate DH results
        shared_secret = dh1 + dh2
        self._zero_memory(dh1)
        self._zero_memory(dh2)

        return shared_secret

    def _derive_key(self, input_key_material: bytes, info: bytes) -> bytes:
        """
        Derive a 32-byte key using HKDF-SHA256.

        Args:
            input_key_material: The key material to derive from.
            info: The info string for this derivation context.

        Returns:
            32-byte derived key.
        """
        hkdf = HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=b"AetherSignal",
            info=info,
            backend=default_backend(),
        )
        return hkdf.derive(input_key_material)

    def _ratchet_send_chain(self, session: SignalSession) -> bytes:
        """
        Advance the send chain key and return the message key.

        Args:
            session: The Signal session.

        Returns:
            32-byte message key.
        """
        h = hmac.HMAC(session.send_chain_key, hashes.SHA256(), backend=default_backend())
        h.update(b"\x01")
        message_key = h.finalize()

        h = hmac.HMAC(session.send_chain_key, hashes.SHA256(), backend=default_backend())
        h.update(b"\x02")
        session.send_chain_key = h.finalize()

        return message_key

    def _ratchet_recv_chain(self, session: SignalSession) -> bytes:
        """
        Advance the receive chain key and return the message key.

        Args:
            session: The Signal session.

        Returns:
            32-byte message key.
        """
        h = hmac.HMAC(session.recv_chain_key, hashes.SHA256(), backend=default_backend())
        h.update(b"\x01")
        message_key = h.finalize()

        h = hmac.HMAC(session.recv_chain_key, hashes.SHA256(), backend=default_backend())
        h.update(b"\x02")
        session.recv_chain_key = h.finalize()

        return message_key

    @staticmethod
    def _export_ec_public_key(public_numbers: ec.EllipticCurvePublicNumbers) -> bytes:
        """Export an ECDH P-256 public key as uncompressed point (65 bytes: 0x04 || X || Y)."""
        x_bytes = public_numbers.x.to_bytes(32, "big")
        y_bytes = public_numbers.y.to_bytes(32, "big")
        return b"\x04" + x_bytes + y_bytes

    @staticmethod
    def _import_ec_public_key(key_bytes: bytes) -> ec.EllipticCurvePublicKey:
        """Import an uncompressed P-256 public key (65 bytes) into an EC public key object."""
        if len(key_bytes) != 65 or key_bytes[0] != 0x04:
            raise ValueError("Invalid uncompressed P-256 public key format")

        x = int.from_bytes(key_bytes[1:33], "big")
        y = int.from_bytes(key_bytes[33:65], "big")

        public_numbers = ec.EllipticCurvePublicNumbers(x, y, ec.SECP256R1())
        return public_numbers.public_key(default_backend())

    @staticmethod
    def _zero_memory(data: bytes) -> None:
        """
        Overwrite a byte array with zeros for security.

        Note: Python doesn't allow true in-place memory zeroing of immutable bytes,
        but this serves as a placeholder for the security intent.
        """
        # In production, you might use ctypes or similar for true zeroing
        # For now, we just ensure the reference is cleared
        pass
