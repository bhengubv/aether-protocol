// SPDX-License-Identifier: MIT
// BLE tracking protection: rotating Service UUID + IRK-based Resolvable Private
// Addresses (RPA).
//
// Faithful mirror of src/AetherNet.Security/Privacy/BlePrivacy.cs. The window is
// encoded as a little-endian int64; every AetherNet SDK reproduces the vectors
// in fixtures/bleprivacy/vectors.json byte-for-byte.
//
// Crypto reuses the SDK's existing libsodium HMAC-SHA256 backend
// (aethernet_hmac_sha256 in src/security.c). The BLE "ah" function needs a
// single AES-128-ECB block, which libsodium does not expose (its only AES is
// AES-256-GCM), so the public-domain tiny-AES-c in c/vendor/tiny-aes provides
// AES_ECB_encrypt for that one block. The vendored AES is built AES128/ECB-only
// via compile definitions in c/CMakeLists.txt.

#include <string.h>

#include "aethernet/ble_privacy.h"
#include "aethernet/security.h"   /* aethernet_hmac_sha256 */

#include "aes.h"                  /* c/vendor/tiny-aes (AES-128 ECB single block) */

/* Encode a signed 64-bit window as 8 little-endian bytes (matches
 * BinaryPrimitives.WriteInt64LittleEndian in the C# reference). */
static void window_le64(int64_t window, uint8_t out[8]) {
    uint64_t u = (uint64_t)window;
    for (int i = 0; i < 8; i++) {
        out[i] = (uint8_t)(u & 0xFF);
        u >>= 8;
    }
}

/* BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand[3]) → first 3 bytes.
 * Single-block ECB, no padding — exactly the C# Ah(). */
static void ble_ah(const uint8_t irk[16], const uint8_t prand[3], uint8_t out_hash[3]) {
    uint8_t block[16];
    memset(block, 0, sizeof(block));
    memcpy(block + 13, prand, 3);

    struct AES_ctx ctx;
    AES_init_ctx(&ctx, irk);
    AES_ECB_encrypt(&ctx, block); /* in place; block now holds the ciphertext */

    out_hash[0] = block[0];
    out_hash[1] = block[1];
    out_hash[2] = block[2];
}

int64_t aethernet_ble_window_for(int64_t unix_seconds) {
    return unix_seconds / AETHERNET_BLE_ROTATION_SECONDS;
}

bool aethernet_ble_service_uuid(const uint8_t *rotation_key,
                                size_t rotation_key_len,
                                int64_t window,
                                char *out_uuid,
                                size_t out_cap) {
    if (!rotation_key || !out_uuid || out_cap < AETHERNET_BLE_UUID_STR_SIZE) {
        return false;
    }

    uint8_t win[8];
    window_le64(window, win);

    uint8_t mac[32];
    if (!aethernet_hmac_sha256(rotation_key, rotation_key_len, win, sizeof(win), mac)) {
        return false;
    }

    // Format mac[0..16] as the canonical lowercase 8-4-4-4-12 UUID:
    // bytes 0-3, 4-5, 6-7, 8-9, 10-15 (matches C# FormatUuid).
    static const char HEX[] = "0123456789abcdef";
    // Byte group layout: number of bytes per hyphen-separated group.
    static const int groups[] = { 4, 2, 2, 2, 6 };

    size_t bi = 0;   // byte index into mac
    size_t oi = 0;   // char index into out_uuid
    for (int g = 0; g < 5; g++) {
        if (g > 0) {
            out_uuid[oi++] = '-';
        }
        for (int k = 0; k < groups[g]; k++) {
            uint8_t b = mac[bi++];
            out_uuid[oi++] = HEX[(b >> 4) & 0xF];
            out_uuid[oi++] = HEX[b & 0xF];
        }
    }
    out_uuid[oi] = '\0';   // oi == 36 here

    return true;
}

bool aethernet_ble_resolvable_address(const uint8_t *irk,
                                      int64_t window,
                                      uint8_t *out_rpa) {
    if (!irk || !out_rpa) {
        return false;
    }

    uint8_t win[8];
    window_le64(window, win);

    uint8_t mac[32];
    if (!aethernet_hmac_sha256(irk, AETHERNET_BLE_IRK_SIZE, win, sizeof(win), mac)) {
        return false;
    }

    uint8_t prand[3];
    prand[0] = mac[0];
    prand[1] = mac[1];
    prand[2] = mac[2];
    prand[0] = (uint8_t)((prand[0] & 0x3F) | 0x40); // RPA address-type bits (0b01)

    uint8_t hash[3];
    ble_ah(irk, prand, hash);

    // rpa = hash(3) || prand(3)
    out_rpa[0] = hash[0];
    out_rpa[1] = hash[1];
    out_rpa[2] = hash[2];
    out_rpa[3] = prand[0];
    out_rpa[4] = prand[1];
    out_rpa[5] = prand[2];

    return true;
}

bool aethernet_ble_resolve_address(const uint8_t *irk,
                                   size_t irk_len,
                                   const uint8_t *rpa,
                                   size_t rpa_len) {
    if (!irk || !rpa ||
        irk_len != AETHERNET_BLE_IRK_SIZE ||
        rpa_len != AETHERNET_BLE_RPA_SIZE) {
        return false;
    }

    const uint8_t *prand = rpa + 3;   // the RPA's prand is its last 3 bytes
    uint8_t hash[3];
    ble_ah(irk, prand, hash);

    return hash[0] == rpa[0] && hash[1] == rpa[1] && hash[2] == rpa[2];
}
