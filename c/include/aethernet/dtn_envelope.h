// SPDX-License-Identifier: MIT
// Canonical binary DTN envelope (wire format v1).
//
// Byte-identical across all eight AetherNet SDKs; the Go encoder
// (go/cmd/dtnfixturegen) is the oracle and fixtures/dtn/expected/*.bin pins
// the bytes. Every multi-byte integer is LITTLE-ENDIAN, except the 16-byte
// bundle id which is the raw RFC-4122 big-endian UUID (the aethernet_dtn_bundle
// id[] is already stored in that form). Cleartext routing fields come first and
// the opaque encrypted_payload is last (the future T1 privacy seam).

#ifndef AETHERNET_DTN_ENVELOPE_H
#define AETHERNET_DTN_ENVELOPE_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/dtn.h"
#include "aethernet/protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

#define AETHERNET_DTN_ENVELOPE_VERSION 0x01

/**
 * Serialize a bundle to the canonical binary DTN envelope. On success writes a
 * malloc'd buffer to *out (caller frees) and its length to *out_len, returning
 * true. Returns false on allocation failure.
 */
bool aethernet_dtn_bundle_encode(const aethernet_dtn_bundle_t *bundle,
                                 uint8_t **out, uint32_t *out_len);

/**
 * Deserialize a binary DTN bundle envelope into a newly-allocated bundle
 * (caller frees via aethernet_dtn_bundle_free). Returns NULL on a malformed,
 * truncated, wrong-version, or out-of-range envelope.
 */
aethernet_dtn_bundle_t *aethernet_dtn_bundle_decode(const uint8_t *data, uint32_t len);

/**
 * Custody-ack (18 bytes fixed): version | bundle_id(16) | accepted(u8 0/1).
 */
bool aethernet_dtn_custody_ack_encode(const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                                      bool accepted, uint8_t **out, uint32_t *out_len);
bool aethernet_dtn_custody_ack_decode(const uint8_t *data, uint32_t len,
                                      uint8_t out_bundle_id[AETHERNET_PACKET_ID_SIZE],
                                      bool *out_accepted);

/**
 * Delivery-receipt: version | bundle_id(16) | recipient_uhid(u16+utf8) |
 * total_hops(i32) | total_custody_transfers(i32) | delivered_at_ms(i64).
 * On decode, *out_recipient_uhid is a malloc'd NUL-terminated string the caller
 * must free.
 */
bool aethernet_dtn_delivery_receipt_encode(const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                                           const char *recipient_uhid,
                                           int32_t total_hops,
                                           int32_t total_custody_transfers,
                                           int64_t delivered_at_ms,
                                           uint8_t **out, uint32_t *out_len);
bool aethernet_dtn_delivery_receipt_decode(const uint8_t *data, uint32_t len,
                                           uint8_t out_bundle_id[AETHERNET_PACKET_ID_SIZE],
                                           char **out_recipient_uhid,
                                           int32_t *out_total_hops,
                                           int32_t *out_total_custody_transfers,
                                           int64_t *out_delivered_at_ms);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_DTN_ENVELOPE_H
