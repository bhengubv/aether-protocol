// SPDX-License-Identifier: MIT
// Transport-backed WebRTC signalling carrier.
//
// Carries WebRTC SDP/ICE signalling over an existing aethernet_transport_t —
// typically the AetherNet QUIC/HTTP relay (c/src/relay_transport.c) or the
// serverless circuit-relay fallback, but the in-process transport works too — so
// two SEPARATE nodes can negotiate a direct data channel without a dedicated
// signalling server. Once the channel is open the app traffic flows peer-to-peer;
// only the short handshake ever touches the carrier's transport.
//
// This is the C mirror of the C# RelayWebRtcSignaling
// (src/AetherNet.Transport.WebRtc/RelayWebRtcSignaling.cs) and plugs into the
// same signalling seam the in-memory bus uses: it produces an
// aethernet_webrtc_signaling_t (send + set_handler) that
// aethernet_webrtc_transport_new() drives.
//
// WIRE FRAMING (byte-identical to C# for cross-language interop):
//   [ 'A' 'W' 'S' '1' ]  4-byte magic  ("Aether WebRtc Signal", framing v1)
//   [ JSON body ]        compact JSON, PascalCase keys, numeric Type enum:
//        {"FromUhid":..,"ToUhid":..,"Type":0|1|2,"Sdp":..,"Candidate":..,
//         "SdpMLineIndex":0,"SdpMid":..}
//     Sdp is present for OFFER/ANSWER; Candidate + SdpMid for CANDIDATE; the
//     null-valued string fields are omitted (matching C#'s WhenWritingNull).
//
// Inbound bytes on the transport that do NOT start with the AWS1 magic are
// ignored — they are ordinary application traffic, not signalling. Give the
// carrier a transport whose data path is dedicated to signalling (e.g. a relay
// connection reserved for control traffic) so the prefixed frames never collide
// with the application data path.
//
// This carrier changes NO mesh wire-serialization and NO fixtures: WebRTC
// signalling is out-of-band, on the transport's opaque byte channel.

#ifndef AETHERNET_WEBRTC_SIGNALING_H
#define AETHERNET_WEBRTC_SIGNALING_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "aethernet/transport.h"
#include "aethernet/transport_webrtc.h"  /* aethernet_webrtc_signaling_t / _signal_t */

#ifdef __cplusplus
extern "C" {
#endif

/* The 4-byte framing magic, exposed so tests / other carriers can assert on it.
 * "AWS1" == Aether WebRtc Signal, framing v1 — identical bytes to the C# Magic. */
#define AETHERNET_WEBRTC_SIGNAL_MAGIC_0 'A'
#define AETHERNET_WEBRTC_SIGNAL_MAGIC_1 'W'
#define AETHERNET_WEBRTC_SIGNAL_MAGIC_2 'S'
#define AETHERNET_WEBRTC_SIGNAL_MAGIC_3 '1'
#define AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN 4

/**
 * Opaque transport-backed signalling carrier.
 *
 * One carrier wraps exactly one aethernet_transport_t (the "signalling wire").
 * It exposes an aethernet_webrtc_signaling_t whose send() frames each signal as
 * AWS1 + JSON and pushes it onto the transport, and whose set_handler() receives
 * inbound frames the carrier parsed off the transport's data callback.
 */
typedef struct aethernet_webrtc_signaling_carrier aethernet_webrtc_signaling_carrier_t;

/**
 * Create a signalling carrier riding `channel`.
 *
 * The carrier registers its own on-data-received callback on `channel` (via
 * aethernet_transport_set_on_data_received), so the caller must not also claim
 * that callback for the same transport instance — dedicate the transport to
 * signalling. The carrier does NOT take ownership of `channel`; the caller
 * destroys the transport after the carrier.
 *
 * Returns NULL on allocation failure or if `channel` is NULL.
 * Free with aethernet_webrtc_signaling_carrier_destroy().
 */
aethernet_webrtc_signaling_carrier_t *
aethernet_webrtc_signaling_carrier_new(aethernet_transport_t *channel);

/**
 * The aethernet_webrtc_signaling_t view of the carrier — hand this to
 * aethernet_webrtc_transport_new(). Owned by the carrier; valid until the
 * carrier is destroyed. Returns NULL if `carrier` is NULL.
 */
aethernet_webrtc_signaling_t *
aethernet_webrtc_signaling_carrier_iface(aethernet_webrtc_signaling_carrier_t *carrier);

/**
 * Detach the transport callback and free the carrier. Safe on NULL.
 */
void aethernet_webrtc_signaling_carrier_destroy(aethernet_webrtc_signaling_carrier_t *carrier);

/* ───────────────────────── framing primitives (exposed for tests) ─────────
 *
 * These let a test drive the exact wire bytes without standing up a transport,
 * and let other carriers reuse the identical AWS1 framing.
 */

/**
 * Encode `signal` as AWS1 + compact JSON into a freshly malloc'd buffer.
 * On success returns the buffer (caller frees) and writes its length to
 * *out_len. Returns NULL on allocation failure or if signal is NULL.
 */
uint8_t *aethernet_webrtc_signal_frame_encode(const aethernet_webrtc_signal_t *signal,
                                              size_t *out_len);

/**
 * Decode an AWS1-framed signal from `data`/`data_len` into `out_signal`.
 * Returns true only if the bytes start with the AWS1 magic AND the JSON body
 * parses. Non-magic bytes (ordinary app traffic) return false without touching
 * *out_signal beyond zeroing. Malformed-after-magic bytes also return false.
 */
bool aethernet_webrtc_signal_frame_decode(const uint8_t *data, size_t data_len,
                                          aethernet_webrtc_signal_t *out_signal);

/** True iff `data`/`data_len` begins with the 4-byte AWS1 magic. */
bool aethernet_webrtc_signal_frame_has_magic(const uint8_t *data, size_t data_len);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_WEBRTC_SIGNALING_H */
