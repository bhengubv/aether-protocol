// SPDX-License-Identifier: MIT
// Aether ERID-announce mesh binding (PacketType EridAnnounce 56). See aethernet/erid_announce.h.
//
// Thin directed transport: send an already-Signal-encrypted ERID announcement to one peer, and surface
// inbound announcements (still encrypted) via a callback. Mirrors the green C# EridAnnounceService.
//
// The payload is OPAQUE — a Signal EncryptedPayload whose plaintext is an EridAnnouncementCodec frame
// (see erid.c). This layer never frames, encrypts, decrypts, or inspects it: it copies the blob into a
// directed packet on send and hands the borrowed bytes to the callback on receive. Binary, no JSON.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex.

#include "aethernet/erid_announce.h"
#include "aethernet/constants.h"

#include <stdlib.h>
#include <string.h>

// ─── Internal state ──────────────────────────────────────

struct aethernet_erid_announce_service {
    aethernet_mesh_sender_t *sender;

    aethernet_erid_announce_received_cb received_cb;
    void                               *received_cb_user_data;
};

// ─── Public API ──────────────────────────────────────────

aethernet_erid_announce_service_t *aethernet_erid_announce_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_erid_announce_service_t *svc =
        (aethernet_erid_announce_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_erid_announce_service_free(aethernet_erid_announce_service_t *service) {
    if (!service) return;
    free(service);
}

void aethernet_erid_announce_set_received_cb(aethernet_erid_announce_service_t *service,
                                             aethernet_erid_announce_received_cb cb,
                                             void *user_data) {
    if (!service) return;
    service->received_cb = cb;
    service->received_cb_user_data = user_data;
}

bool aethernet_erid_announce_send(aethernet_erid_announce_service_t *service,
                                  const char *peer_uhid,
                                  const uint8_t *encrypted_announcement,
                                  uint32_t len) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !encrypted_announcement || len == 0)
        return false;                              // mirrors the C# ArgumentException guards
    if (!service->sender->send) return false;      // host wired no directed send — cannot deliver

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_ERID_ANNOUNCE;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, encrypted_announcement, len);  // copies the opaque blob

    bool delivered = service->sender->send(service->sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

bool aethernet_erid_announce_handle_packet(aethernet_erid_announce_service_t *service,
                                           const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_ERID_ANNOUNCE) return false;  // wrong type → false
    if (packet->payload == NULL || packet->payload_len == 0) return false;  // empty body → false

    if (service->received_cb) {
        aethernet_erid_announce_received_t evt;
        evt.encrypted_announcement = packet->payload;  // borrowed for the callback duration
        evt.len = packet->payload_len;
        evt.from_uhid = packet->source_uhid ? packet->source_uhid : "";
        service->received_cb(&evt, service->received_cb_user_data);
    }
    return true;
}
