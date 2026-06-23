// SPDX-License-Identifier: MIT
// Aether WebRTC P2P Transport — real RTCDataChannel transport via libdatachannel.
//
// Direct peer-to-peer transport over a WebRTC data channel (libdatachannel, C API).
// NAT traversal is handled by ICE/STUN; the SDP/ICE handshake rides an injected
// signalling abstraction, so no central signalling server is required. This is C's
// first real, internet-capable transport (the others are in-process simulations).
//
// Mirrors src/AetherNet.Transport.WebRtc/ (SIPSorcery, C#) and go/transport/webrtc/
// (pion, Go): a transport satisfying the aethernet_transport_vtable_t, a Signal type
// + Signaling abstraction, and an in-memory signalling bus that routes signals
// in-process by UHID.

#ifndef AETHERNET_TRANSPORT_WEBRTC_H
#define AETHERNET_TRANSPORT_WEBRTC_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "aethernet/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ───────────────────────── Signalling abstraction ─────────────────────────
 *
 * A single WebRTC signalling message — the SDP offer/answer or an ICE candidate
 * two peers must exchange before a direct data channel can open. Carried by a
 * aethernet_webrtc_signaling_t channel (the in-memory bus here; a real relay /
 * mesh / SMS ignition link in production) — never a central signalling server.
 */

typedef enum {
    AETHERNET_WEBRTC_SIGNAL_OFFER     = 0,  /* SDP offer from the initiating peer.   */
    AETHERNET_WEBRTC_SIGNAL_ANSWER    = 1,  /* SDP answer from the responding peer.  */
    AETHERNET_WEBRTC_SIGNAL_CANDIDATE = 2,  /* A trickled ICE candidate.             */
} aethernet_webrtc_signal_type_t;

/* UHIDs are bounded the same way the in-process transport bounds them. */
#define AETHERNET_WEBRTC_UHID_MAX 128
/* SDP blobs (single data m-section, host-only ICE) stay well under this. */
#define AETHERNET_WEBRTC_SDP_MAX  8192
/* A single ICE candidate line plus its mid. */
#define AETHERNET_WEBRTC_CAND_MAX 1024
#define AETHERNET_WEBRTC_MID_MAX  256

/**
 * One WebRTC signalling message. SDP fields are set for OFFER/ANSWER; the
 * candidate fields are set for CANDIDATE. Stored by value (fixed buffers) so the
 * bus can queue a signal without owning heap allocations.
 */
typedef struct {
    char                           from_uhid[AETHERNET_WEBRTC_UHID_MAX];
    char                           to_uhid[AETHERNET_WEBRTC_UHID_MAX];
    aethernet_webrtc_signal_type_t type;
    char                           sdp[AETHERNET_WEBRTC_SDP_MAX];   /* OFFER / ANSWER */
    char                           candidate[AETHERNET_WEBRTC_CAND_MAX]; /* CANDIDATE */
    char                           sdp_mid[AETHERNET_WEBRTC_MID_MAX];    /* CANDIDATE */
} aethernet_webrtc_signal_t;

/**
 * Handler invoked for a signal addressed to the local node. The signal is owned
 * by the caller (the bus); copy anything you need to retain.
 */
typedef void (*aethernet_webrtc_signal_handler)(const aethernet_webrtc_signal_t *signal,
                                                 void *user_data);

/**
 * Carries WebRTC SDP/ICE signalling between two peers by UHID. The transport
 * sends signals via send() and receives them via the registered handler. A
 * vtable so any channel (the in-memory bus, a relay, the mesh) can back it.
 */
typedef struct aethernet_webrtc_signaling {
    void *handle;  /* implementation state */

    /* Deliver a signal to its addressee (signal->to_uhid). Returns true if the
     * signal was handed to the underlying channel. */
    bool (*send)(void *handle, const aethernet_webrtc_signal_t *signal);

    /* Register the handler invoked for signals addressed to the local node. */
    void (*set_handler)(void *handle,
                        aethernet_webrtc_signal_handler handler,
                        void *user_data);
} aethernet_webrtc_signaling_t;

/* ───────────────────────── In-memory signalling bus ───────────────────────
 *
 * Routes signals between endpoints by UHID, in process, with no server. The
 * reference Signaling implementation — backing same-process simulations and the
 * test suite. Each endpoint delivers inbound signals on its own pump thread, in
 * send order, so a signal never re-enters the sender's call stack.
 */

typedef struct aethernet_webrtc_signaling_bus aethernet_webrtc_signaling_bus_t;

/** Create an empty bus. Free with aethernet_webrtc_signaling_bus_destroy(). */
aethernet_webrtc_signaling_bus_t *aethernet_webrtc_signaling_bus_new(void);

/**
 * Return (creating once) the signalling endpoint for uhid. The returned pointer
 * is owned by the bus and stays valid until the bus is destroyed.
 */
aethernet_webrtc_signaling_t *
aethernet_webrtc_signaling_bus_endpoint(aethernet_webrtc_signaling_bus_t *bus,
                                        const char *uhid);

/** Stop all endpoint pumps and free the bus. */
void aethernet_webrtc_signaling_bus_destroy(aethernet_webrtc_signaling_bus_t *bus);

/* ───────────────────────── WebRTC transport ───────────────────────────────*/

/**
 * Create a WebRTC P2P transport for local_uhid, driving the SDP/ICE handshake
 * over the given signalling channel.
 *
 * ice_servers: an array of STUN/TURN URL strings (e.g. "stun:stun.l.google.com:19302"),
 * or NULL. Pass ice_server_count == 0 to force host-candidate-only ICE (same-LAN
 * / tests, no network dependency) — matching the empty-list contract of the C#
 * and Go references.
 *
 * The returned aethernet_transport_t satisfies the standard vtable (send /
 * is_connected / set_on_data_received / destroy / metrics), so TransportManager
 * ranks it like any other transport. Free with aethernet_transport_destroy().
 *
 * Returns NULL on error (no local_uhid, no signalling, or a libdatachannel
 * peer-connection failure).
 */
aethernet_transport_t *
aethernet_webrtc_transport_new(const char *local_uhid,
                               aethernet_webrtc_signaling_t *signaling,
                               const char *const *ice_servers,
                               size_t ice_server_count);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_TRANSPORT_WEBRTC_H
