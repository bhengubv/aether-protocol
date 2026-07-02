// SPDX-License-Identifier: MIT
// Aether ERID-announce mesh binding — directed EridAnnounce (56) transport.
//
// Binds PacketType EridAnnounce (56) to the mesh: a node shares its rotating-address routing key with
// an established peer by sending the (already Signal-encrypted) announcement directly. Transport only
// — the plaintext framing (the EridAnnouncementCodec in erid.c) and the encryption (Signal) are done
// by the host/EridExchangeService; this service just carries the opaque encrypted blob as a directed
// packet and surfaces inbound ones via a callback. Binary payload (no JSON). Mirrors the green C#
// EridAnnounceService.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex (matches sos.c / channels.c / prekey.c).

#ifndef AETHERNET_ERID_ANNOUNCE_H
#define AETHERNET_ERID_ANNOUNCE_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A received ERID announcement surfaced to the host. Mirrors the C# EridAnnounceReceived. The body is
 * a Signal EncryptedPayload whose plaintext is an EridAnnouncementCodec frame — opaque to this layer.
 * `encrypted_announcement`/`from_uhid` are borrowed for the callback duration (they point into the
 * inbound packet); copy anything you wish to retain past the callback.
 */
typedef struct {
    const uint8_t *encrypted_announcement; // borrowed; the opaque Signal-encrypted announcement bytes
    uint32_t       len;                    // length of encrypted_announcement in bytes
    const char    *from_uhid;              // borrowed; peer that sent the announcement
} aethernet_erid_announce_received_t;

/**
 * ERID-announce callback. Fired once per inbound EridAnnounce packet. `event` is borrowed for the
 * callback duration — copy any fields to retain. Mirrors the C# AnnounceReceived event.
 */
typedef void (*aethernet_erid_announce_received_cb)(const aethernet_erid_announce_received_t *event,
                                                    void *user_data);

/**
 * Opaque ERID-announce service handle. Sends directed EridAnnounce packets and surfaces inbound ones
 * via the announce-received callback. The service borrows `sender` — caller keeps it alive for the
 * service lifetime.
 */
typedef struct aethernet_erid_announce_service aethernet_erid_announce_service_t;

aethernet_erid_announce_service_t *aethernet_erid_announce_service_new(aethernet_mesh_sender_t *sender);
void aethernet_erid_announce_service_free(aethernet_erid_announce_service_t *service);

/**
 * Send an (already-encrypted) ERID announcement directly to `peer_uhid`: build a directed EridAnnounce
 * packet (dest peer_uhid, TTL AETHERNET_DEFAULT_TTL) whose opaque payload is a copy of
 * `encrypted_announcement[0..len)`, and dispatch it via sender->send. Returns the delivery result.
 * Returns false if `service`/`peer_uhid`/`encrypted_announcement` is NULL, `peer_uhid` is empty, `len`
 * is 0, the host wired no directed send, or delivery fails. Mirrors the C# SendAnnounceAsync.
 */
bool aethernet_erid_announce_send(aethernet_erid_announce_service_t *service,
                                  const char *peer_uhid,
                                  const uint8_t *encrypted_announcement,
                                  uint32_t len);

/**
 * Process an inbound EridAnnounce packet: fire the announce-received callback with the opaque payload
 * and the packet source, return true. Returns false for the wrong packet type, an empty/NULL body, or
 * a NULL argument. Mirrors the C# HandleAsync.
 */
bool aethernet_erid_announce_handle_packet(aethernet_erid_announce_service_t *service,
                                           const aethernet_mesh_packet_t *packet);

/** Set the announce-received callback (fired on each inbound EridAnnounce). Pass NULL to clear. */
void aethernet_erid_announce_set_received_cb(aethernet_erid_announce_service_t *service,
                                             aethernet_erid_announce_received_cb cb,
                                             void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_ERID_ANNOUNCE_H
