# SPDX-License-Identifier: MIT

"""Ed25519 signing service using PyNaCl (libsodium)."""

import nacl.signing
import nacl.exceptions
from typing import Tuple


class Ed25519SigningService:
    """
    Static Ed25519 signing service using PyNaCl/libsodium.

    Key format:
    - Private key: 32-byte seed
    - Public key: 32-byte point
    - Signature: 64-byte signature
    """

    @staticmethod
    def generate_keypair() -> Tuple[bytes, bytes]:
        """
        Generate a new Ed25519 key pair.

        Returns:
            A tuple of (private_key: 32 bytes, public_key: 32 bytes).
        """
        signing_key = nacl.signing.SigningKey.generate()
        private_key = bytes(signing_key)
        public_key = bytes(signing_key.verify_key)
        return private_key, public_key

    @staticmethod
    def sign(private_key: bytes, data: bytes) -> bytes:
        """
        Sign data using an Ed25519 private key.

        Args:
            private_key: 32-byte Ed25519 seed.
            data: The data to sign.

        Returns:
            64-byte Ed25519 signature.

        Raises:
            ValueError: If private_key is not 32 bytes.
        """
        if private_key is None:
            raise ValueError("private_key cannot be None")
        if data is None:
            raise ValueError("data cannot be None")

        if len(private_key) != 32:
            raise ValueError(
                f"Ed25519 private key must be 32 bytes, got {len(private_key)}"
            )

        signing_key = nacl.signing.SigningKey(private_key)
        signed_message = signing_key.sign(data)
        # Extract just the signature (first 64 bytes of the signed message)
        return signed_message.signature

    @staticmethod
    def verify(public_key: bytes, data: bytes, signature: bytes) -> bool:
        """
        Verify an Ed25519 signature.

        Args:
            public_key: 32-byte Ed25519 public key.
            data: The signed data.
            signature: 64-byte Ed25519 signature.

        Returns:
            True if the signature is valid, False otherwise.
        """
        if public_key is None or data is None or signature is None:
            return False

        if len(public_key) != 32:
            return False

        if len(signature) != 64:
            return False

        try:
            verify_key = nacl.signing.VerifyKey(public_key)
            verify_key.verify(data, signature)
            return True
        except (nacl.exceptions.BadSignatureError, ValueError):
            return False

    @staticmethod
    def verify_with_fallback(
        public_key: bytes, data: bytes, signature: bytes
    ) -> bool:
        """
        Verify a signature, trying Ed25519 first and falling back to legacy P-256
        ECDSA for public keys longer than 32 bytes (Protocol Version 1 identity keys
        during the migration window — see PROTOCOL_SPEC.md section 7.5).

        Args:
            public_key: 32-byte Ed25519 public key, or a DER-encoded
                SubjectPublicKeyInfo P-256 public key (> 32 bytes).
            data: The signed data.
            signature: 64-byte Ed25519 signature, or an ASN.1 DER ECDSA signature.

        Returns:
            True if the signature is valid under whichever scheme the key selects.
        """
        if public_key is None or data is None or signature is None:
            return False
        if len(public_key) == 32:
            return Ed25519SigningService.verify(public_key, data, signature)
        return Ed25519SigningService._verify_p256(public_key, data, signature)

    @staticmethod
    def _verify_p256(
        spki_public_key: bytes, data: bytes, signature: bytes
    ) -> bool:
        """
        Verify a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
        Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
        """
        try:
            from cryptography.exceptions import InvalidSignature
            from cryptography.hazmat.primitives import hashes
            from cryptography.hazmat.primitives.asymmetric import ec
            from cryptography.hazmat.primitives.serialization import (
                load_der_public_key,
            )
        except ImportError:
            return False

        try:
            pub = load_der_public_key(spki_public_key)
            if not isinstance(pub, ec.EllipticCurvePublicKey) or not isinstance(
                pub.curve, ec.SECP256R1
            ):
                return False
            pub.verify(signature, data, ec.ECDSA(hashes.SHA256()))
            return True
        except (InvalidSignature, ValueError, TypeError):
            return False
