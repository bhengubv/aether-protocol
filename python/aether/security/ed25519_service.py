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
        Verify a signature with fallback support for legacy keys.

        For now, this only supports Ed25519. Future versions may add P-256 ECDSA
        fallback during migration windows.

        Args:
            public_key: Public key bytes (32 = Ed25519).
            data: The signed data.
            signature: The signature bytes.

        Returns:
            True if the signature is valid.
        """
        return Ed25519SigningService.verify(public_key, data, signature)
