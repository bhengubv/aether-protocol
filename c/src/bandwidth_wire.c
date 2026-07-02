// SPDX-License-Identifier: MIT
// AetherNet Bandwidth Measurement Framework (ABMF) — WIRE bindings for the mesh.
// PacketType BandwidthProbe (53) / BandwidthAck (54) / BandwidthGossip (55).
//
// C port of the green C# BandwidthWireService / BandwidthWireCodec (src/AetherNet.Core/Bandwidth/
// BandwidthWireService.cs). Probes and acks are directed; gossip is broadcast. All bodies are
// BINARY LITTLE-ENDIAN with no version byte — the LE put/get helpers below mirror the DTN binary
// serializer's convention (c/src/dtn_envelope.c put_u16/put_i32/put_i64), reused here as a matching
// local pair (this SDK keeps codec helpers file-local; there is no exported LE header to depend on).
// Byte-identity gate: fixtures/bandwidth/vectors.json.
//
// Decode has NO JSON parser (unlike prekey.c) — the bodies are fixed-size binary, so there is no
// cJSON parse tree and thus no use-after-free hazard. Every read is still bounds-checked against the
// supplied length, and the one owned string the service copies (the gossip peer uhid, stamped from
// the packet source) is duplicated into service-owned storage before the callback returns.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex (matches prekey.c / sos.c / channels.c).

#include "aethernet/bandwidth_wire.h"
#include "aethernet/constants.h"

#include <stdlib.h>
#include <string.h>

// ─── Little-endian codec helpers ─────────────────────────
// Mirror dtn_envelope.c's put_* (LSB-first). The get_* counterparts read the same layout back.

static void bw_put_u32(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
    p[2] = (uint8_t)((v >> 16) & 0xff);
    p[3] = (uint8_t)((v >> 24) & 0xff);
}

static void bw_put_i32(uint8_t *p, int32_t v) {
    bw_put_u32(p, (uint32_t)v);
}

static void bw_put_i64(uint8_t *p, int64_t v) {
    uint64_t u = (uint64_t)v;
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)((u >> (8 * i)) & 0xff);
}

static uint32_t bw_get_u32(const uint8_t *p) {
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static int32_t bw_get_i32(const uint8_t *p) {
    return (int32_t)bw_get_u32(p);
}

static int64_t bw_get_i64(const uint8_t *p) {
    uint64_t u = 0;
    for (int i = 0; i < 8; i++) u |= (uint64_t)p[i] << (8 * i);
    return (int64_t)u;
}

// Clamp a 64-bit value to the signed 32-bit range [0, INT32_MAX] — matches the C#
// Math.Clamp(g.RtPropUs, 0, int.MaxValue) applied before the i32 write in SerializeGossip.
static int32_t bw_clamp_i32_nonneg(int64_t v) {
    if (v < 0) return 0;
    if (v > (int64_t)INT32_MAX) return INT32_MAX;
    return (int32_t)v;
}

// ─── Codec ───────────────────────────────────────────────

size_t aethernet_bw_wire_probe_serialize(const aethernet_bw_probe_t *probe,
                                         uint8_t                    *out,
                                         size_t                      out_cap) {
    if (!probe || !out || out_cap < AETHERNET_BW_WIRE_PROBE_SIZE) return 0;
    bw_put_u32(out + 0, probe->sequence);
    bw_put_i64(out + 4, probe->sender_send_us);
    return AETHERNET_BW_WIRE_PROBE_SIZE;
}

bool aethernet_bw_wire_probe_deserialize(const uint8_t         *data,
                                         size_t                 len,
                                         aethernet_bw_probe_t  *out) {
    if (!data || !out || len < AETHERNET_BW_WIRE_PROBE_SIZE) return false;
    memset(out, 0, sizeof(*out));
    out->sequence       = bw_get_u32(data + 0);
    out->sender_send_us = bw_get_i64(data + 4);
    return true;
}

size_t aethernet_bw_wire_ack_serialize(const aethernet_bw_probe_ack_t *ack,
                                       uint8_t                        *out,
                                       size_t                          out_cap) {
    if (!ack || !out || out_cap < AETHERNET_BW_WIRE_ACK_SIZE) return 0;
    bw_put_u32(out + 0,  ack->sequence);
    bw_put_i64(out + 4,  ack->sender_send_us);
    bw_put_i64(out + 12, ack->receiver_receive_us);
    bw_put_i64(out + 20, ack->receiver_send_us);
    bw_put_i32(out + 28, ack->probe_bytes);
    // sender_receive_us / rtt_us / forward_owd_us are local-only — deliberately NOT written.
    return AETHERNET_BW_WIRE_ACK_SIZE;
}

bool aethernet_bw_wire_ack_deserialize(const uint8_t            *data,
                                       size_t                    len,
                                       aethernet_bw_probe_ack_t *out) {
    if (!data || !out || len < AETHERNET_BW_WIRE_ACK_SIZE) return false;
    memset(out, 0, sizeof(*out));
    out->sequence             = bw_get_u32(data + 0);
    out->sender_send_us       = bw_get_i64(data + 4);
    out->receiver_receive_us  = bw_get_i64(data + 12);
    out->receiver_send_us     = bw_get_i64(data + 20);
    out->probe_bytes          = bw_get_i32(data + 28);
    out->sender_receive_us    = 0;  // filled by the prober on receipt, not on the wire
    // rtt_us / forward_owd_us left 0 by the memset — host calls compute_derived() after stamping.
    return true;
}

size_t aethernet_bw_wire_gossip_serialize(const aethernet_bw_gossip_t *gossip,
                                          uint8_t                     *out,
                                          size_t                       out_cap) {
    if (!gossip || !out || out_cap < AETHERNET_BW_WIRE_GOSSIP_SIZE) return 0;
    bw_put_i64(out + 0, gossip->btlbw_bps);
    bw_put_i32(out + 8, bw_clamp_i32_nonneg(gossip->rtprop_us));
    out[12] = (uint8_t)gossip->confidence;
    // peer_uhid / transport_name / measured_at_unix_ms are not on the wire.
    return AETHERNET_BW_WIRE_GOSSIP_SIZE;
}

bool aethernet_bw_wire_gossip_deserialize(const uint8_t         *data,
                                          size_t                 len,
                                          aethernet_bw_gossip_t *out) {
    if (!data || !out || len < AETHERNET_BW_WIRE_GOSSIP_SIZE) return false;
    memset(out, 0, sizeof(*out));
    out->btlbw_bps  = bw_get_i64(data + 0);
    out->rtprop_us  = (int64_t)bw_get_i32(data + 8);
    out->confidence = (aethernet_bw_confidence_t)data[12];
    // peer_uhid[0] / transport_name[0] are '\0' from the memset; the service stamps peer_uhid.
    return true;
}

// ─── Service state ───────────────────────────────────────

struct aethernet_bw_wire_service {
    aethernet_mesh_sender_t *sender;   // borrowed

    aethernet_bw_probe_received_cb  probe_cb;
    void                           *probe_cb_user_data;
    aethernet_bw_ack_received_cb    ack_cb;
    void                           *ack_cb_user_data;
    aethernet_bw_gossip_received_cb gossip_cb;
    void                           *gossip_cb_user_data;
};

// ─── Directed send / broadcast ───────────────────────────

// Build and directed-send a bandwidth packet of `type` carrying `body` to `peer_uhid`. Copies body
// into the packet. Returns the delivery result from sender->send (false if the host wired none).
// Mirrors prekey.c send_pre_key_packet and the C# SendDirectedAsync (MeshPacket + SendAsync).
static bool send_directed(aethernet_bw_wire_service_t *service,
                          aethernet_packet_type_t      type,
                          const char                  *peer_uhid,
                          const uint8_t               *body,
                          uint32_t                     body_len) {
    if (!service->sender->send) return false;  // host wired no directed send

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)type;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);

    bool delivered = service->sender->send(service->sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

// ─── Public API ──────────────────────────────────────────

aethernet_bw_wire_service_t *aethernet_bw_wire_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_bw_wire_service_t *svc =
        (aethernet_bw_wire_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_bw_wire_service_free(aethernet_bw_wire_service_t *service) {
    free(service);
}

bool aethernet_bw_wire_service_send_probe(aethernet_bw_wire_service_t *service,
                                          const char                  *peer_uhid,
                                          const aethernet_bw_probe_t  *probe) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !probe) return false;
    uint8_t body[AETHERNET_BW_WIRE_PROBE_SIZE];
    size_t n = aethernet_bw_wire_probe_serialize(probe, body, sizeof(body));
    if (n != AETHERNET_BW_WIRE_PROBE_SIZE) return false;
    return send_directed(service, AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE, peer_uhid, body, (uint32_t)n);
}

bool aethernet_bw_wire_service_send_ack(aethernet_bw_wire_service_t    *service,
                                        const char                     *peer_uhid,
                                        const aethernet_bw_probe_ack_t *ack) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !ack) return false;
    uint8_t body[AETHERNET_BW_WIRE_ACK_SIZE];
    size_t n = aethernet_bw_wire_ack_serialize(ack, body, sizeof(body));
    if (n != AETHERNET_BW_WIRE_ACK_SIZE) return false;
    return send_directed(service, AETHERNET_PACKET_TYPE_BANDWIDTH_ACK, peer_uhid, body, (uint32_t)n);
}

bool aethernet_bw_wire_service_broadcast_gossip(aethernet_bw_wire_service_t *service,
                                                const aethernet_bw_gossip_t *gossip,
                                                int                         *out_count) {
    if (!service || !gossip) return false;
    if (!service->sender->broadcast) return false;  // host wired no broadcast

    uint8_t body[AETHERNET_BW_WIRE_GOSSIP_SIZE];
    size_t n = aethernet_bw_wire_gossip_serialize(gossip, body, sizeof(body));
    if (n != AETHERNET_BW_WIRE_GOSSIP_SIZE) return false;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_BANDWIDTH_GOSSIP;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, (uint32_t)n);

    int reached = service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);

    if (out_count) *out_count = reached;
    return true;
}

bool aethernet_bw_wire_service_handle_packet(aethernet_bw_wire_service_t   *service,
                                             const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;

    const uint8_t *payload = packet->payload;
    size_t         plen    = packet->payload_len;

    switch (packet->type) {
        case AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE: {
            aethernet_bw_probe_t probe;
            if (!aethernet_bw_wire_probe_deserialize(payload, plen, &probe)) return false;
            if (service->probe_cb) {
                aethernet_bw_probe_received_t evt;
                evt.probe     = probe;
                evt.from_uhid = packet->source_uhid ? packet->source_uhid : "";
                service->probe_cb(&evt, service->probe_cb_user_data);
            }
            return true;
        }

        case AETHERNET_PACKET_TYPE_BANDWIDTH_ACK: {
            aethernet_bw_probe_ack_t ack;
            if (!aethernet_bw_wire_ack_deserialize(payload, plen, &ack)) return false;
            if (service->ack_cb)
                service->ack_cb(&ack, service->ack_cb_user_data);
            return true;
        }

        case AETHERNET_PACKET_TYPE_BANDWIDTH_GOSSIP: {
            aethernet_bw_gossip_t gossip;
            if (!aethernet_bw_wire_gossip_deserialize(payload, plen, &gossip)) return false;
            // Stamp peer_uhid from the packet source (the C# `gossip with { PeerUhid = ... }`). The
            // struct field is a fixed 128-byte buffer, not a pointer — copy the packet source in,
            // truncating safely, so nothing borrowed from `packet` outlives the callback.
            if (packet->source_uhid) {
                size_t cap = sizeof(gossip.peer_uhid);
                strncpy(gossip.peer_uhid, packet->source_uhid, cap - 1);
                gossip.peer_uhid[cap - 1] = '\0';
            } else {
                gossip.peer_uhid[0] = '\0';
            }
            if (service->gossip_cb)
                service->gossip_cb(&gossip, service->gossip_cb_user_data);
            return true;
        }

        default:
            return false;  // wrong packet type
    }
}

void aethernet_bw_wire_service_set_probe_received_cb(aethernet_bw_wire_service_t   *service,
                                                     aethernet_bw_probe_received_cb cb,
                                                     void                          *user_data) {
    if (!service) return;
    service->probe_cb = cb;
    service->probe_cb_user_data = user_data;
}

void aethernet_bw_wire_service_set_ack_received_cb(aethernet_bw_wire_service_t *service,
                                                   aethernet_bw_ack_received_cb cb,
                                                   void                        *user_data) {
    if (!service) return;
    service->ack_cb = cb;
    service->ack_cb_user_data = user_data;
}

void aethernet_bw_wire_service_set_gossip_received_cb(aethernet_bw_wire_service_t    *service,
                                                      aethernet_bw_gossip_received_cb cb,
                                                      void                           *user_data) {
    if (!service) return;
    service->gossip_cb = cb;
    service->gossip_cb_user_data = user_data;
}
