// SPDX-License-Identifier: MIT
// WebRTC P2P Transport — real RTCDataChannel transport over libdatachannel (C API).
//
// A direct internet-capable transport for AetherNet. NAT traversal is ICE/STUN;
// the SDP/ICE handshake rides an injected signalling channel, so no central
// signalling server is required. Mirrors the C# (SIPSorcery) and Go (pion)
// references: a transport satisfying aethernet_transport_vtable_t, the Signal /
// Signaling abstraction, and an in-memory signalling bus.
//
// libdatachannel is C++ internally but exposes a stable C API (rtc/rtc.h). Peer
// connections and data channels are referenced by integer id; per-id user
// pointers (rtcSetUserPointer) carry our peer-link context into the callbacks.

#define _POSIX_C_SOURCE 200809L

#include <stdlib.h>
#include <string.h>
#include <pthread.h>

#include <rtc/rtc.h>

#include "aethernet/transport_webrtc.h"

#define AETHERNET_WEBRTC_DATA_CHANNEL_LABEL "aether"
#define AETHERNET_WEBRTC_MAX_PEERS          256
/* Open-wait budget (ms). Matches the 20 s ConnectTimeout in the references. */
#define AETHERNET_WEBRTC_CONNECT_TIMEOUT_MS 20000

/* ───────────────────────── one connection to a single peer ─────────────────*/

typedef struct aethernet_webrtc_transport aethernet_webrtc_transport_t;

typedef struct {
    aethernet_webrtc_transport_t *owner;     /* parent transport (for signalling + onData) */
    char  peer_uhid[AETHERNET_WEBRTC_UHID_MAX];

    int   pc;                                /* libdatachannel peer-connection id */
    int   dc;                                /* libdatachannel data-channel id (-1 until attached) */

    bool  open;                              /* data channel is open */
    bool  closed;                            /* terminal (failed/closed/disconnected) */
    bool  in_use;                            /* slot occupied */

    pthread_mutex_t  state_lock;             /* guards open/closed + cond */
    pthread_cond_t   state_cv;               /* signalled on open or closed */
} aethernet_webrtc_peer_link_t;

/* ───────────────────────── transport state ────────────────────────────────*/

struct aethernet_webrtc_transport {
    char local_uhid[AETHERNET_WEBRTC_UHID_MAX];

    aethernet_webrtc_signaling_t *signaling;

    /* ICE servers, owned copies (the rtcConfiguration borrows these pointers
     * only for the duration of rtcCreatePeerConnection, but we keep them for the
     * transport's lifetime to be safe). */
    char  **ice_servers;
    size_t  ice_server_count;

    /* Receive surface — the same callback shape every transport uses. */
    aethernet_transport_on_data_received on_data;
    void                                *on_data_user;

    aethernet_webrtc_peer_link_t peers[AETHERNET_WEBRTC_MAX_PEERS];

    aethernet_transport_metrics_t metrics;

    bool            closed;
    pthread_mutex_t lock;   /* guards peers[], on_data, closed */
};

/* ───────────────────────── time helper ────────────────────────────────────*/

#include <time.h>

static void deadline_after_ms(struct timespec *out, long ms) {
    clock_gettime(CLOCK_REALTIME, out);
    out->tv_sec  += ms / 1000;
    out->tv_nsec += (ms % 1000) * 1000000L;
    if (out->tv_nsec >= 1000000000L) {
        out->tv_sec  += 1;
        out->tv_nsec -= 1000000000L;
    }
}

/* ───────────────────────── peer-link helpers ──────────────────────────────*/

/* Caller must hold transport->lock. Finds an in-use link by UHID, or NULL. */
static aethernet_webrtc_peer_link_t *
find_link_locked(aethernet_webrtc_transport_t *t, const char *peer_uhid) {
    for (size_t i = 0; i < AETHERNET_WEBRTC_MAX_PEERS; i++) {
        if (t->peers[i].in_use && strcmp(t->peers[i].peer_uhid, peer_uhid) == 0)
            return &t->peers[i];
    }
    return NULL;
}

/* Caller must hold transport->lock. Returns a free slot, or NULL if full. */
static aethernet_webrtc_peer_link_t *alloc_link_locked(aethernet_webrtc_transport_t *t) {
    for (size_t i = 0; i < AETHERNET_WEBRTC_MAX_PEERS; i++) {
        if (!t->peers[i].in_use)
            return &t->peers[i];
    }
    return NULL;
}

static void mark_open(aethernet_webrtc_peer_link_t *l) {
    pthread_mutex_lock(&l->state_lock);
    l->open = true;
    pthread_cond_broadcast(&l->state_cv);
    pthread_mutex_unlock(&l->state_lock);
}

static void mark_closed(aethernet_webrtc_peer_link_t *l) {
    pthread_mutex_lock(&l->state_lock);
    if (!l->closed) {
        l->closed = true;
        pthread_cond_broadcast(&l->state_cv);  /* wake any waiter — it will see !open */
    }
    pthread_mutex_unlock(&l->state_lock);
}

/* Wait until the data channel opens or the link fails or the timeout elapses.
 * Returns true only if the channel is open. */
static bool wait_open(aethernet_webrtc_peer_link_t *l, long timeout_ms) {
    struct timespec deadline;
    deadline_after_ms(&deadline, timeout_ms);

    pthread_mutex_lock(&l->state_lock);
    while (!l->open && !l->closed) {
        int rc = pthread_cond_timedwait(&l->state_cv, &l->state_lock, &deadline);
        if (rc != 0) break;  /* ETIMEDOUT */
    }
    bool open = l->open;
    pthread_mutex_unlock(&l->state_lock);
    return open;
}

/* ───────────────────────── libdatachannel callbacks ───────────────────────
 *
 * Every callback receives the user pointer we registered for the pc / dc id,
 * which is the owning peer-link. Callbacks run on libdatachannel's internal
 * threads, so they only touch the link's own state_lock (never the transport
 * lock) to avoid lock-ordering deadlocks.
 */

static void RTC_API on_local_description(int pc, const char *sdp, const char *type, void *ptr) {
    (void)pc;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (!l || !sdp || !type) return;

    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof(sig));
    strncpy(sig.from_uhid, l->owner->local_uhid, sizeof(sig.from_uhid) - 1);
    strncpy(sig.to_uhid, l->peer_uhid, sizeof(sig.to_uhid) - 1);
    sig.type = (strcmp(type, "offer") == 0)
                   ? AETHERNET_WEBRTC_SIGNAL_OFFER
                   : AETHERNET_WEBRTC_SIGNAL_ANSWER;
    strncpy(sig.sdp, sdp, sizeof(sig.sdp) - 1);

    if (l->owner->signaling && l->owner->signaling->send)
        l->owner->signaling->send(l->owner->signaling->handle, &sig);
}

static void RTC_API on_local_candidate(int pc, const char *cand, const char *mid, void *ptr) {
    (void)pc;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (!l || !cand) return;

    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof(sig));
    strncpy(sig.from_uhid, l->owner->local_uhid, sizeof(sig.from_uhid) - 1);
    strncpy(sig.to_uhid, l->peer_uhid, sizeof(sig.to_uhid) - 1);
    sig.type = AETHERNET_WEBRTC_SIGNAL_CANDIDATE;
    strncpy(sig.candidate, cand, sizeof(sig.candidate) - 1);
    if (mid) strncpy(sig.sdp_mid, mid, sizeof(sig.sdp_mid) - 1);

    if (l->owner->signaling && l->owner->signaling->send)
        l->owner->signaling->send(l->owner->signaling->handle, &sig);
}

static void RTC_API on_pc_state_change(int pc, rtcState state, void *ptr) {
    (void)pc;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (!l) return;
    if (state == RTC_DISCONNECTED || state == RTC_FAILED || state == RTC_CLOSED)
        mark_closed(l);
}

static void RTC_API on_message(int id, const char *message, int size, void *ptr) {
    (void)id;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (!l || !message) return;

    /* libdatachannel convention: size < 0 => null-terminated string message;
     * size >= 0 => binary message of `size` bytes. We send binary, but handle
     * both so a string-sending peer still surfaces bytes. */
    size_t len = (size < 0) ? strlen(message) : (size_t)size;

    /* Read the receive callback under the transport lock, then invoke it
     * outside the lock (the handler may re-enter the transport). */
    aethernet_webrtc_transport_t *t = l->owner;
    pthread_mutex_lock(&t->lock);
    aethernet_transport_on_data_received cb = t->on_data;
    void *user = t->on_data_user;
    pthread_mutex_unlock(&t->lock);

    if (cb)
        cb(l->peer_uhid, (const uint8_t *)message, len, user);
}

static void RTC_API on_dc_open(int id, void *ptr) {
    (void)id;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (l) mark_open(l);
}

/* Responder path: the remote created the channel; libdatachannel hands it to us. */
static void RTC_API on_data_channel(int pc, int dc, void *ptr) {
    (void)pc;
    aethernet_webrtc_peer_link_t *l = (aethernet_webrtc_peer_link_t *)ptr;
    if (!l) return;
    l->dc = dc;
    rtcSetUserPointer(dc, l);
    rtcSetOpenCallback(dc, on_dc_open);
    rtcSetMessageCallback(dc, on_message);
    if (rtcIsOpen(dc))
        mark_open(l);
}

/* ───────────────────────── link lifecycle ─────────────────────────────────*/

/* Initialise a peer-connection for the link. Caller holds transport->lock and
 * has already claimed the slot (in_use = true, peer_uhid set). Returns true on
 * success; on failure the slot is left for the caller to release. */
static bool link_init_pc(aethernet_webrtc_transport_t *t, aethernet_webrtc_peer_link_t *l) {
    pthread_mutex_init(&l->state_lock, NULL);
    pthread_cond_init(&l->state_cv, NULL);
    l->owner  = t;
    l->dc     = -1;
    l->open   = false;
    l->closed = false;

    rtcConfiguration config;
    memset(&config, 0, sizeof(config));
    /* ice_server_count == 0 => host-candidate-only ICE (no STUN), exactly the
     * empty-list contract the C# / Go references use for same-LAN + tests. */
    if (t->ice_server_count > 0) {
        config.iceServers      = (const char **)t->ice_servers;
        config.iceServersCount = (int)t->ice_server_count;
    }
    config.disableAutoNegotiation = false;

    int pc = rtcCreatePeerConnection(&config);
    if (pc < 0)
        return false;

    l->pc = pc;
    rtcSetUserPointer(pc, l);
    rtcSetLocalDescriptionCallback(pc, on_local_description);
    rtcSetLocalCandidateCallback(pc, on_local_candidate);
    rtcSetStateChangeCallback(pc, on_pc_state_change);
    rtcSetDataChannelCallback(pc, on_data_channel);  /* responder receives the channel */
    return true;
}

/* Initiator: create the data channel + send the offer (libdatachannel emits the
 * offer SDP via the local-description callback once the channel exists, because
 * auto-negotiation is on). */
static bool link_start_initiator(aethernet_webrtc_peer_link_t *l) {
    int dc = rtcCreateDataChannel(l->pc, AETHERNET_WEBRTC_DATA_CHANNEL_LABEL);
    if (dc < 0)
        return false;
    l->dc = dc;
    rtcSetUserPointer(dc, l);
    rtcSetOpenCallback(dc, on_dc_open);
    rtcSetMessageCallback(dc, on_message);
    if (rtcIsOpen(dc))
        mark_open(l);
    return true;
}

/* Release a link's libdatachannel + pthread resources. Caller holds transport->lock. */
static void link_teardown_locked(aethernet_webrtc_peer_link_t *l) {
    if (!l->in_use) return;
    if (l->dc >= 0) {
        rtcClose(l->dc);
        rtcDeleteDataChannel(l->dc);
        l->dc = -1;
    }
    if (l->pc >= 0) {
        rtcClosePeerConnection(l->pc);
        rtcDeletePeerConnection(l->pc);
        l->pc = -1;
    }
    mark_closed(l);
    pthread_cond_destroy(&l->state_cv);
    pthread_mutex_destroy(&l->state_lock);
    memset(l->peer_uhid, 0, sizeof(l->peer_uhid));
    l->in_use = false;
    l->open   = false;
    l->closed = false;
}

/* Get an existing open-or-pending link for peer_uhid, or create one. as_initiator
 * controls whether we open the channel + send the offer (true) or wait for an
 * inbound offer (false). Returns the link (still under no lock), or NULL. */
static aethernet_webrtc_peer_link_t *
get_or_create_link(aethernet_webrtc_transport_t *t, const char *peer_uhid, bool as_initiator) {
    pthread_mutex_lock(&t->lock);
    if (t->closed) {
        pthread_mutex_unlock(&t->lock);
        return NULL;
    }

    aethernet_webrtc_peer_link_t *existing = find_link_locked(t, peer_uhid);
    if (existing) {
        bool dead;
        pthread_mutex_lock(&existing->state_lock);
        dead = existing->closed;
        pthread_mutex_unlock(&existing->state_lock);
        if (!dead) {
            pthread_mutex_unlock(&t->lock);
            if (as_initiator)
                wait_open(existing, AETHERNET_WEBRTC_CONNECT_TIMEOUT_MS);
            return existing;
        }
        /* Closed — reclaim the slot and fall through to a fresh link. */
        link_teardown_locked(existing);
    }

    aethernet_webrtc_peer_link_t *l = alloc_link_locked(t);
    if (!l) {
        pthread_mutex_unlock(&t->lock);
        return NULL;
    }
    l->in_use = true;
    strncpy(l->peer_uhid, peer_uhid, sizeof(l->peer_uhid) - 1);
    l->peer_uhid[sizeof(l->peer_uhid) - 1] = '\0';

    if (!link_init_pc(t, l)) {
        l->in_use = false;
        memset(l->peer_uhid, 0, sizeof(l->peer_uhid));
        pthread_mutex_unlock(&t->lock);
        return NULL;
    }
    pthread_mutex_unlock(&t->lock);

    /* Start the handshake outside the transport lock — libdatachannel may fire
     * the local-description / candidate callbacks synchronously. */
    if (as_initiator) {
        if (!link_start_initiator(l)) {
            pthread_mutex_lock(&t->lock);
            link_teardown_locked(l);
            pthread_mutex_unlock(&t->lock);
            return NULL;
        }
        wait_open(l, AETHERNET_WEBRTC_CONNECT_TIMEOUT_MS);
    }
    return l;
}

/* ───────────────────────── signalling inbound ─────────────────────────────*/

static void on_signal_received(const aethernet_webrtc_signal_t *signal, void *user_data) {
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)user_data;
    if (!t || !signal) return;
    if (strcmp(signal->to_uhid, t->local_uhid) != 0) return;

    switch (signal->type) {
        case AETHERNET_WEBRTC_SIGNAL_OFFER: {
            aethernet_webrtc_peer_link_t *l =
                get_or_create_link(t, signal->from_uhid, /*as_initiator=*/false);
            if (l) {
                /* Setting the remote offer is enough: with auto-negotiation on,
                 * libdatachannel creates the answer itself and emits it through
                 * the local-description callback (see examples/copy-paste-capi).
                 * Calling rtcSetLocalDescription here would be redundant. */
                rtcSetRemoteDescription(l->pc, signal->sdp, "offer");
            }
            break;
        }
        case AETHERNET_WEBRTC_SIGNAL_ANSWER: {
            pthread_mutex_lock(&t->lock);
            aethernet_webrtc_peer_link_t *l = find_link_locked(t, signal->from_uhid);
            int pc = l ? l->pc : -1;
            pthread_mutex_unlock(&t->lock);
            if (pc >= 0)
                rtcSetRemoteDescription(pc, signal->sdp, "answer");
            break;
        }
        case AETHERNET_WEBRTC_SIGNAL_CANDIDATE: {
            pthread_mutex_lock(&t->lock);
            aethernet_webrtc_peer_link_t *l = find_link_locked(t, signal->from_uhid);
            int pc = l ? l->pc : -1;
            pthread_mutex_unlock(&t->lock);
            if (pc >= 0 && signal->candidate[0] != '\0') {
                const char *mid = signal->sdp_mid[0] != '\0' ? signal->sdp_mid : NULL;
                rtcAddRemoteCandidate(pc, signal->candidate, mid);
            }
            break;
        }
    }
}

/* ───────────────────────── vtable methods ─────────────────────────────────*/

static bool webrtc_send(void *handle, const char *peer_uhid,
                        const uint8_t *data, size_t data_len) {
    if (!handle || !peer_uhid || !data || data_len == 0) return false;
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)handle;

    aethernet_webrtc_peer_link_t *l = get_or_create_link(t, peer_uhid, /*as_initiator=*/true);
    if (!l) return false;

    if (!wait_open(l, AETHERNET_WEBRTC_CONNECT_TIMEOUT_MS)) {
        aethernet_transport_metrics_record_sample(&t->metrics, 0, false, 0);
        return false;
    }

    /* Binary message: size >= 0 tells libdatachannel this is binary, not a
     * null-terminated string. */
    int rc = rtcSendMessage(l->dc, (const char *)data, (int)data_len);
    bool ok = (rc >= 0);
    aethernet_transport_metrics_record_sample(&t->metrics, ok ? 1 : 0, ok,
                                              ok ? (uint64_t)data_len : 0);
    return ok;
}

static bool webrtc_is_connected(void *handle, const char *peer_uhid) {
    if (!handle || !peer_uhid) return false;
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)handle;

    pthread_mutex_lock(&t->lock);
    aethernet_webrtc_peer_link_t *l = find_link_locked(t, peer_uhid);
    bool open = false;
    if (l) {
        pthread_mutex_lock(&l->state_lock);
        open = l->open && !l->closed;
        pthread_mutex_unlock(&l->state_lock);
    }
    pthread_mutex_unlock(&t->lock);
    return open;
}

static void webrtc_set_on_data_received(void *handle,
                                        aethernet_transport_on_data_received callback,
                                        void *user_data) {
    if (!handle) return;
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)handle;
    pthread_mutex_lock(&t->lock);
    t->on_data      = callback;
    t->on_data_user = user_data;
    pthread_mutex_unlock(&t->lock);
}

static aethernet_transport_metrics_t *webrtc_get_metrics(void *handle) {
    if (!handle) return NULL;
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)handle;
    return &t->metrics;
}

static void webrtc_destroy(void *handle) {
    if (!handle) return;
    aethernet_webrtc_transport_t *t = (aethernet_webrtc_transport_t *)handle;

    /* Detach signalling first so no inbound signal spins up a new link mid-teardown. */
    if (t->signaling && t->signaling->set_handler)
        t->signaling->set_handler(t->signaling->handle, NULL, NULL);

    pthread_mutex_lock(&t->lock);
    t->closed = true;
    for (size_t i = 0; i < AETHERNET_WEBRTC_MAX_PEERS; i++) {
        if (t->peers[i].in_use)
            link_teardown_locked(&t->peers[i]);
    }
    pthread_mutex_unlock(&t->lock);

    pthread_mutex_destroy(&t->lock);

    for (size_t i = 0; i < t->ice_server_count; i++)
        free(t->ice_servers[i]);
    free(t->ice_servers);
    free(t);
}

/* ───────────────────────── construction ───────────────────────────────────*/

aethernet_transport_t *
aethernet_webrtc_transport_new(const char *local_uhid,
                               aethernet_webrtc_signaling_t *signaling,
                               const char *const *ice_servers,
                               size_t ice_server_count) {
    if (!local_uhid || !local_uhid[0] || !signaling) return NULL;

    aethernet_transport_t *transport =
        (aethernet_transport_t *)malloc(sizeof(aethernet_transport_t));
    if (!transport) return NULL;

    aethernet_webrtc_transport_t *state =
        (aethernet_webrtc_transport_t *)malloc(sizeof(aethernet_webrtc_transport_t));
    if (!state) {
        free(transport);
        return NULL;
    }
    memset(state, 0, sizeof(*state));

    strncpy(state->local_uhid, local_uhid, sizeof(state->local_uhid) - 1);
    state->signaling = signaling;
    pthread_mutex_init(&state->lock, NULL);
    aethernet_transport_metrics_init(&state->metrics);

    /* Own a copy of each ICE server URL. */
    if (ice_server_count > 0 && ice_servers) {
        state->ice_servers = (char **)calloc(ice_server_count, sizeof(char *));
        if (!state->ice_servers) {
            pthread_mutex_destroy(&state->lock);
            free(state);
            free(transport);
            return NULL;
        }
        for (size_t i = 0; i < ice_server_count; i++) {
            const char *src = ice_servers[i] ? ice_servers[i] : "";
            size_t n = strlen(src);
            state->ice_servers[i] = (char *)malloc(n + 1);
            if (!state->ice_servers[i]) {
                for (size_t j = 0; j < i; j++) free(state->ice_servers[j]);
                free(state->ice_servers);
                pthread_mutex_destroy(&state->lock);
                free(state);
                free(transport);
                return NULL;
            }
            memcpy(state->ice_servers[i], src, n + 1);
        }
        state->ice_server_count = ice_server_count;
    }

    aethernet_transport_vtable_t *vtable =
        (aethernet_transport_vtable_t *)malloc(sizeof(aethernet_transport_vtable_t));
    if (!vtable) {
        for (size_t i = 0; i < state->ice_server_count; i++) free(state->ice_servers[i]);
        free(state->ice_servers);
        pthread_mutex_destroy(&state->lock);
        free(state);
        free(transport);
        return NULL;
    }
    memset(vtable, 0, sizeof(*vtable));
    vtable->name                = "WebRTC P2P";
    vtable->send                = webrtc_send;
    vtable->is_connected        = webrtc_is_connected;
    vtable->set_on_data_received = webrtc_set_on_data_received;
    vtable->destroy             = webrtc_destroy;
    vtable->get_metrics         = webrtc_get_metrics;
    /* Direct internet link — bounded by the local NIC; ranked between the radio
     * mesh (cheap, proximity) and the QUIC/HTTP relay (last resort). */
    vtable->max_bandwidth_bps   = 100000000;  /* 100 Mbps */
    vtable->power_cost_relative = 45;
    vtable->max_range_meters    = 0;          /* internet — unbounded */

    transport->vtable = vtable;
    transport->handle = state;

    /* Register the inbound-signal handler last, once state is fully built. */
    if (signaling->set_handler)
        signaling->set_handler(signaling->handle, on_signal_received, state);

    return transport;
}
