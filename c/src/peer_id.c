// SPDX-License-Identifier: MIT
//
// PeerId derivation — C implementation.
//
// Mirrors src/AetherNet.Core/Identity/PeerId.cs and go/identity/peerid.go
// byte-for-byte. Self-contained: depends only on the C standard library and its
// own header (no other repo headers, no POSIX), so the primitive can be lifted
// out and verified standalone.

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "aethernet/peer_id.h"

// Bitcoin base58 alphabet (no 0, O, I, l).
static const char BASE58_ALPHABET[] =
    "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

// identity-multihash(code 0x00, len 0x24=36) || protobuf PublicKey(type Ed25519:
// 0x08 0x01; data len 32: 0x12 0x20).
static const uint8_t ED25519_PREFIX[] = {0x00, 0x24, 0x08, 0x01, 0x12, 0x20};
#define ED25519_PREFIX_LEN 6

// 6-byte prefix + 32-byte key.
#define MULTIHASH_LEN (ED25519_PREFIX_LEN + AETHERNET_ED25519_PUBLIC_KEY_LENGTH)

// Divides the big-endian base-256 number in number[firstDigit..len) by 58, in
// place, and returns the remainder. Bytes are treated as unsigned.
static int divmod58(uint8_t *number, size_t first_digit, size_t len) {
    int remainder = 0;
    for (size_t i = first_digit; i < len; i++) {
        int temp = remainder * 256 + (int)number[i];
        number[i] = (uint8_t)(temp / 58);
        remainder = temp % 58;
    }
    return remainder;
}

// Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading
// '1's. Writes a NUL-terminated string into out (caller guarantees capacity).
static void base58_encode(const uint8_t *input, size_t input_len, char *out) {
    if (input_len == 0) {
        out[0] = '\0';
        return;
    }

    // Count leading zero bytes.
    size_t zeros = 0;
    while (zeros < input_len && input[zeros] == 0) zeros++;

    // divmod mutates in place, so work on a copy.
    uint8_t buffer[MULTIHASH_LEN];
    memcpy(buffer, input, input_len);

    // Safe upper bound: log(256)/log(58) < 2, so 2 chars per input byte suffices.
    char encoded[MULTIHASH_LEN * 2];
    size_t encoded_len = input_len * 2;
    size_t output_start = encoded_len;

    for (size_t input_start = zeros; input_start < input_len;) {
        encoded[--output_start] =
            BASE58_ALPHABET[divmod58(buffer, input_start, input_len)];
        if (buffer[input_start] == 0) input_start++;  // a digit fully consumed
    }
    // Drop any extra leading '1's the loop produced.
    while (output_start < encoded_len && encoded[output_start] == BASE58_ALPHABET[0]) {
        output_start++;
    }
    // Re-add one '1' per leading zero byte of the input.
    for (; zeros > 0; zeros--) encoded[--output_start] = BASE58_ALPHABET[0];

    size_t out_len = encoded_len - output_start;
    memcpy(out, encoded + output_start, out_len);
    out[out_len] = '\0';
}

bool aethernet_peer_id_from_ed25519(const uint8_t pubkey[32], char out[64]) {
    if (pubkey == NULL || out == NULL) return false;

    uint8_t multihash[MULTIHASH_LEN];
    memcpy(multihash, ED25519_PREFIX, ED25519_PREFIX_LEN);
    memcpy(multihash + ED25519_PREFIX_LEN, pubkey,
           AETHERNET_ED25519_PUBLIC_KEY_LENGTH);

    base58_encode(multihash, MULTIHASH_LEN, out);
    return true;
}
