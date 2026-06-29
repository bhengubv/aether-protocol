# SPDX-License-Identifier: MIT

"""libp2p **PeerID** derivation — the bridge between an AetherNet identity and the global
libp2p relay / DHT used by the decentralised relay layer.

Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID
is a *pure, deterministic* function of that key — no lookup table, no network. A node can
compute its own PeerID (to announce on the libp2p DHT) and any peer's PeerID (to dial it)
from the public key alone.

Encoding (must be byte-identical across every SDK language)
----------------------------------------------------------
1. ``protobuf PublicKey`` = ``08 01`` (field 1 Type = Ed25519) ``12 20`` (field 2 Data,
   length 32) followed by the 32-byte key — 36 bytes total.
2. ``identity multihash`` = ``00`` (identity hash code) ``24`` (length 36) followed by the
   protobuf — 38 bytes. libp2p uses the identity multihash for keys whose serialized form
   is <= 42 bytes, which Ed25519 always is.
3. ``PeerID string`` = base58btc (Bitcoin alphabet) of the 38-byte multihash, WITHOUT a
   multibase prefix. Always renders as ``12D3Koo...`` for Ed25519.

Verified byte-for-byte against real ``js-libp2p`` output; see ``fixtures/peerid/``.
"""

from __future__ import annotations

# Bitcoin base58 alphabet (no 0, O, I, l).
_BASE58_ALPHABET: str = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"

# identity-multihash(code 0x00, len 0x24=36) || protobuf PublicKey
# (type Ed25519: 08 01; data len 32: 12 20)
_ED25519_PREFIX: bytes = bytes([0x00, 0x24, 0x08, 0x01, 0x12, 0x20])

#: Length in bytes of a raw Ed25519 public key.
ED25519_PUBLIC_KEY_LENGTH: int = 32


def from_ed25519_public_key(public_key: bytes) -> str:
    """Return the libp2p PeerID string (e.g. ``12D3Koo...``) for a 32-byte Ed25519 public key.

    Parameters
    ----------
    public_key:
        The raw 32-byte Ed25519 public key.

    Raises
    ------
    ValueError
        If ``public_key`` is not exactly 32 bytes.
    """
    if len(public_key) != ED25519_PUBLIC_KEY_LENGTH:
        raise ValueError(
            f"Ed25519 public key must be {ED25519_PUBLIC_KEY_LENGTH} bytes, "
            f"got {len(public_key)}."
        )
    multihash = _ED25519_PREFIX + bytes(public_key)
    return _base58_encode(multihash)


def _base58_encode(data: bytes) -> str:
    """Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading '1's.

    Counts leading zero bytes (each becomes a leading '1'), then converts the big-endian
    base-256 number to base-58 by repeated divmod.
    """
    if len(data) == 0:
        return ""

    zeros = 0
    while zeros < len(data) and data[zeros] == 0:
        zeros += 1

    buffer = bytearray(data)  # divmod mutates in place
    encoded = bytearray(len(data) * 2)  # safe upper bound
    output_start = len(encoded)

    input_start = zeros
    while input_start < len(buffer):
        output_start -= 1
        encoded[output_start] = ord(_BASE58_ALPHABET[_divmod58(buffer, input_start)])
        if buffer[input_start] == 0:
            input_start += 1  # a digit fully consumed

    # Drop extra leading '1's the loop may have produced.
    while output_start < len(encoded) and encoded[output_start] == ord(_BASE58_ALPHABET[0]):
        output_start += 1
    # Re-add one '1' per leading zero byte of the input.
    while zeros > 0:
        output_start -= 1
        encoded[output_start] = ord(_BASE58_ALPHABET[0])
        zeros -= 1

    return encoded[output_start:].decode("ascii")


def _divmod58(number: bytearray, first_digit: int) -> int:
    """Divide the big-endian base-256 number in ``number[first_digit:]`` by 58, in place,
    returning the remainder."""
    remainder = 0
    for i in range(first_digit, len(number)):
        temp = remainder * 256 + number[i]
        number[i] = temp // 58
        remainder = temp % 58
    return remainder
