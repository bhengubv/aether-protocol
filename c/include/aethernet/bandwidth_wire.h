// SPDX-License-Identifier: MIT
// AetherNet Bandwidth Measurement Framework (ABMF) — WIRE bindings.
//
// C port of the C# reference src/AetherNet.Core/Bandwidth/BandwidthWireService.cs. Binds the three
// ABMF PacketTypes to the mesh: send probes (directed) + their acks (directed reply), and
// broadcast/receive warm-start gossip. Inbound packets surface via callbacks; the host feeds them
// into the bandwidth estimator (record_probe_result / warm_from_gossip) and replies to probes.
//
// ─── Wire layouts (LITTLE-ENDIAN, no version byte) ───────────────────────────
//   Probe(53)  : sequence u32 | sender_send_us i64                                              (12 B)
//   Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64
//                | probe_bytes i32                                                              (32 B)
//   Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                  (13 B)
//
// sender_receive_us is NOT on the wire — the prober fills it locally on receipt (serialized as
// nothing; deserialized as 0). peer_uhid/transport_name/measured_at of a gossip come from the
// enclosing packet + local clock, not the wire body — the service stamps peer_uhid from the packet
// source on receive. rtprop_us is clamped to [0, INT32_MAX] on serialize (the gossip struct's field
// is int64_t but the wire slot is i32), matching the C# Math.Clamp. Byte-identity gate:
// fixtures/bandwidth/vectors.json (lowercase hex).
//
// Single-threaded reference impl (mirrors prekey.c / sos.c / channels.c); hosts pumping packets from
// multiple threads must wrap the service in their own mutex.

#ifndef AETHERNET_BANDWIDTH_WIRE_H
#define AETHERNET_BANDWIDTH_WIRE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "aethernet/aethernet_bandwidth.h"  // aethernet_bw_probe_ack_t / _gossip_t / _confidence_t
#include "aethernet/protocol.h"
#include "aethernet/routing.h"              // aethernet_mesh_sender_t

#ifdef __cplusplus
extern "C" {
#endif

/* ── BandwidthProbe ──────────────────────────────────────────────────────────
 *
 * A latency/throughput probe request (PacketType BandwidthProbe = 53 body). Mirrors the C#
 * BandwidthProbe(uint Sequence, long SenderSendUs) record. Value type — no heap allocation, no free.
 */
typedef struct {
    uint32_t sequence;
    int64_t  sender_send_us;   /* µs since epoch, sender clock */
} aethernet_bw_probe_t;

/* Fixed wire sizes — asserted by the codec and by the byte-identity tests. */
#define AETHERNET_BW_WIRE_PROBE_SIZE   12
#define AETHERNET_BW_WIRE_ACK_SIZE     32
#define AETHERNET_BW_WIRE_GOSSIP_SIZE  13

/* ── Codec (pure, no allocation) ─────────────────────────────────────────────
 *
 * The serialize functions write into a caller-provided fixed-size buffer and return the number of
 * bytes written (always the corresponding *_SIZE), or 0 on a NULL argument. The deserialize
 * functions bounds-check `len` against the fixed size and return false when the body is too short or
 * an argument is NULL — mirrors the C# FormatException-on-short-buffer, surfaced here as a boolean.
 */

/** Serialize a probe body into `out` (>= AETHERNET_BW_WIRE_PROBE_SIZE bytes). Returns bytes written. */
size_t aethernet_bw_wire_probe_serialize(const aethernet_bw_probe_t *probe,
                                         uint8_t                    *out,
                                         size_t                      out_cap);

/** Deserialize a probe body from `data[0..len)`. Returns true on success (>= 12 B). */
bool aethernet_bw_wire_probe_deserialize(const uint8_t         *data,
                                         size_t                 len,
                                         aethernet_bw_probe_t  *out);

/**
 * Serialize an ack body into `out` (>= AETHERNET_BW_WIRE_ACK_SIZE bytes). Returns bytes written.
 * ack->sender_receive_us and any derived fields are local-only and are NOT written to the wire.
 */
size_t aethernet_bw_wire_ack_serialize(const aethernet_bw_probe_ack_t *ack,
                                       uint8_t                        *out,
                                       size_t                          out_cap);

/**
 * Deserialize an ack body from `data[0..len)` into `out`. Returns true on success (>= 32 B).
 * out->sender_receive_us is set to 0 (filled locally by the prober, not carried on the wire); the
 * derived rtt_us/forward_owd_us are left at 0 — call aethernet_bw_probe_ack_compute_derived() after
 * the host has stamped sender_receive_us if it needs them.
 */
bool aethernet_bw_wire_ack_deserialize(const uint8_t            *data,
                                       size_t                    len,
                                       aethernet_bw_probe_ack_t *out);

/**
 * Serialize a gossip body into `out` (>= AETHERNET_BW_WIRE_GOSSIP_SIZE bytes). Returns bytes written.
 * gossip->rtprop_us is clamped to [0, INT32_MAX] before being written as i32 (matches the C#
 * Math.Clamp). peer_uhid/transport_name/measured_at_unix_ms are NOT written to the wire.
 */
size_t aethernet_bw_wire_gossip_serialize(const aethernet_bw_gossip_t *gossip,
                                          uint8_t                     *out,
                                          size_t                       out_cap);

/**
 * Deserialize a gossip body from `data[0..len)` into `out`. Returns true on success (>= 13 B).
 * out->peer_uhid and out->transport_name are cleared to empty (the service fills peer_uhid from the
 * packet source); out->measured_at_unix_ms is set to 0.
 */
bool aethernet_bw_wire_gossip_deserialize(const uint8_t         *data,
                                          size_t                 len,
                                          aethernet_bw_gossip_t *out);

/* ── Inbound callbacks ───────────────────────────────────────────────────────
 *
 * Fired from aethernet_bw_wire_service_handle_packet(). All pointers are borrowed for the callback
 * duration — copy anything you wish to retain past the call.
 */

/** An inbound probe plus the peer that sent it (so the host can reply with an ack). */
typedef struct {
    aethernet_bw_probe_t  probe;
    const char           *from_uhid;   /* borrowed; packet source */
} aethernet_bw_probe_received_t;

/** Fired on each inbound BandwidthProbe. `event` borrowed for the call. */
typedef void (*aethernet_bw_probe_received_cb)(const aethernet_bw_probe_received_t *event,
                                               void *user_data);

/** Fired on each inbound BandwidthAck. `ack` borrowed for the call (sender_receive_us == 0). */
typedef void (*aethernet_bw_ack_received_cb)(const aethernet_bw_probe_ack_t *ack,
                                             void *user_data);

/** Fired on each inbound BandwidthGossip. `gossip` borrowed; peer_uhid is the packet source. */
typedef void (*aethernet_bw_gossip_received_cb)(const aethernet_bw_gossip_t *gossip,
                                                void *user_data);

/* ── Service ─────────────────────────────────────────────────────────────────
 *
 * Opaque wire-service handle. Sends directed probe/ack packets, broadcasts gossip, and surfaces
 * inbound bandwidth packets via the callbacks. The service borrows `sender` — caller keeps it alive
 * for the service lifetime.
 */
typedef struct aethernet_bw_wire_service aethernet_bw_wire_service_t;

aethernet_bw_wire_service_t *aethernet_bw_wire_service_new(aethernet_mesh_sender_t *sender);
void aethernet_bw_wire_service_free(aethernet_bw_wire_service_t *service);

/**
 * Send a directed BandwidthProbe (PacketType 53) to `peer_uhid` (dest peer_uhid, TTL
 * AETHERNET_DEFAULT_TTL) via sender->send. Returns the delivery result, or false if
 * service/peer_uhid/probe is NULL, peer_uhid is empty, or the host wired no directed send. Mirrors
 * the C# SendProbeAsync.
 */
bool aethernet_bw_wire_service_send_probe(aethernet_bw_wire_service_t *service,
                                          const char                  *peer_uhid,
                                          const aethernet_bw_probe_t  *probe);

/**
 * Send a directed BandwidthAck (PacketType 54) reply to `peer_uhid`. Returns the delivery result, or
 * false on a NULL/empty argument or no directed send. Mirrors the C# SendAckAsync.
 */
bool aethernet_bw_wire_service_send_ack(aethernet_bw_wire_service_t    *service,
                                        const char                     *peer_uhid,
                                        const aethernet_bw_probe_ack_t *ack);

/**
 * Broadcast a BandwidthGossip (PacketType 55, dest "*", TTL AETHERNET_DEFAULT_TTL) warm-start
 * estimate via sender->broadcast. On success writes the fan-out count (peers reached) to
 * *out_count and returns true. Returns false if service/gossip is NULL or the host wired no
 * broadcast (out_count untouched). Mirrors the C# BroadcastGossipAsync (returns peers reached).
 */
bool aethernet_bw_wire_service_broadcast_gossip(aethernet_bw_wire_service_t *service,
                                                const aethernet_bw_gossip_t *gossip,
                                                int                         *out_count);

/**
 * Dispatch an inbound bandwidth packet to the matching callback:
 *   - BandwidthProbe (53)  → decode body, fire probe-received (from_uhid = packet source).
 *   - BandwidthAck   (54)  → decode body, fire ack-received.
 *   - BandwidthGossip(55)  → decode body, stamp peer_uhid = packet source, fire gossip-received.
 * Returns true when a matching packet was decoded and dispatched. Returns false for the wrong packet
 * type, a short/malformed body, or a NULL argument. Mirrors the C# HandleAsync.
 */
bool aethernet_bw_wire_service_handle_packet(aethernet_bw_wire_service_t   *service,
                                             const aethernet_mesh_packet_t *packet);

/** Set the probe-received callback (fired on each inbound BandwidthProbe). Pass NULL to clear. */
void aethernet_bw_wire_service_set_probe_received_cb(aethernet_bw_wire_service_t   *service,
                                                     aethernet_bw_probe_received_cb cb,
                                                     void                          *user_data);

/** Set the ack-received callback (fired on each inbound BandwidthAck). Pass NULL to clear. */
void aethernet_bw_wire_service_set_ack_received_cb(aethernet_bw_wire_service_t *service,
                                                   aethernet_bw_ack_received_cb cb,
                                                   void                        *user_data);

/** Set the gossip-received callback (fired on each inbound BandwidthGossip). Pass NULL to clear. */
void aethernet_bw_wire_service_set_gossip_received_cb(aethernet_bw_wire_service_t    *service,
                                                      aethernet_bw_gossip_received_cb cb,
                                                      void                           *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_BANDWIDTH_WIRE_H
