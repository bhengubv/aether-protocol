// SPDX-License-Identifier: MIT
// BLE tracking protection: rotating Service UUID + IRK-based Resolvable Private
// Addresses (RPA) for an AetherNet mesh node.

#ifndef AETHERNET_BLE_PRIVACY_H
#define AETHERNET_BLE_PRIVACY_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
 * Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
 * peers without exposing a stable, trackable Bluetooth fingerprint on the air.
 *
 *   - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
 *     shared rotation key and the current time window. Every node in the same
 *     window derives the same UUID, so peers still find each other — but a
 *     passive scanner sees an identifier that changes and cannot be linked over
 *     time.
 *   - The node's stable id is removed from the advertisement; a peer that holds
 *     the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
 *     6-byte RPA instead (the BLE "ah" function).
 *
 * The window-based operations are deterministic and byte-identical across every
 * AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
 * window is encoded as a little-endian int64. This C port is the faithful
 * mirror of src/AetherNet.Security/Privacy/BlePrivacy.cs.
 *
 * Crypto reuses the SDK's existing libsodium HMAC-SHA256
 * (aethernet_hmac_sha256, src/security.c); the "ah" function needs single-block
 * AES-128-ECB, which libsodium does not expose, so a public-domain tiny-AES-c
 * (c/vendor/tiny-aes) provides AES_ECB_encrypt for that one block.
 */

/** Rotation period in seconds (15 minutes). */
#define AETHERNET_BLE_ROTATION_SECONDS 900

/** IRK length in bytes (128-bit Identity Resolving Key). */
#define AETHERNET_BLE_IRK_SIZE 16U

/** Resolvable Private Address length in bytes (hash(3) || prand(3)). */
#define AETHERNET_BLE_RPA_SIZE 6U

/**
 * Length (including the NUL terminator) of the canonical lowercase UUID string
 * "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx": 36 chars + 1 NUL.
 */
#define AETHERNET_BLE_UUID_STR_SIZE 37U

/**
 * The rotation window index for a Unix-seconds timestamp (unix_seconds / 900).
 * Matches BlePrivacy.WindowFor.
 */
int64_t aethernet_ble_window_for(int64_t unix_seconds);

/**
 * The rotating BLE Service UUID for a rotation key and time window. Every node
 * sharing the rotation key derives the same UUID within the window, enabling
 * mutual discovery with no static identifier on the air.
 *
 * uuid = HMAC-SHA256(rotation_key, le64(window))[0..16], formatted as the
 * canonical lowercase 8-4-4-4-12 UUID string.
 *
 * Parameters:
 *   rotation_key     — shared rotation key bytes (any length; HMAC key).
 *   rotation_key_len — length of rotation_key in bytes.
 *   window           — rotation window index (see aethernet_ble_window_for).
 *   out_uuid         — caller-allocated buffer of at least
 *                      AETHERNET_BLE_UUID_STR_SIZE (37) bytes; receives a
 *                      NUL-terminated lowercase UUID string.
 *   out_cap          — capacity of out_uuid in bytes.
 *
 * Returns: true on success. false if rotation_key or out_uuid is NULL, out_cap
 *          is too small, or the underlying HMAC fails.
 */
bool aethernet_ble_service_uuid(const uint8_t *rotation_key,
                                size_t rotation_key_len,
                                int64_t window,
                                char *out_uuid,
                                size_t out_cap);

/**
 * A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
 * hash(3) || prand(3), where prand is HMAC-derived (with the RPA address-type
 * bits set) and hash = AES-128(IRK, prand-block). Rotates every window; only a
 * peer holding the IRK can link successive addresses.
 *
 *   prand = HMAC-SHA256(irk, le64(window))[0..3]
 *   prand[0] = (prand[0] & 0x3F) | 0x40      (RPA address-type bits 0b01)
 *   hash  = ah(irk, prand)                    (AES-128-ECB, first 3 bytes)
 *   rpa   = hash[0..3] || prand[0..3]
 *
 * Parameters:
 *   irk      — the 16-byte Identity Resolving Key.
 *   window   — rotation window index.
 *   out_rpa  — caller-allocated AETHERNET_BLE_RPA_SIZE (6) bytes.
 *
 * Returns: true on success. false if irk or out_rpa is NULL, or the HMAC fails.
 *          (The IRK must be 16 bytes; callers pass a 16-byte buffer.)
 */
bool aethernet_ble_resolvable_address(const uint8_t *irk,
                                      int64_t window,
                                      uint8_t *out_rpa);

/**
 * True if `rpa` (6 bytes) was generated from `irk` (16 bytes) — i.e. this node
 * recognises the peer behind the rotating address. Recomputes ah(irk, prand)
 * over the RPA's prand (its last 3 bytes) and compares the first 3 bytes to the
 * RPA's hash. Length-guarded: returns false unless irk is 16 and rpa is 6 bytes
 * and both pointers are non-NULL.
 */
bool aethernet_ble_resolve_address(const uint8_t *irk,
                                   size_t irk_len,
                                   const uint8_t *rpa,
                                   size_t rpa_len);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_BLE_PRIVACY_H
