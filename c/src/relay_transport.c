// SPDX-License-Identifier: MIT
// Native circuit-relay-v2 ENGINE — see aethernet/relay_transport.h.
//
// Faithful port of go/circuitrelay/transport.go (the clearest reference) and the C#
// CircuitRelayTransportService. All mutable state lives behind a single pthread_mutex;
// the client CONNECT/RESERVE waits block on a pthread_cond, woken when the matching
// response frame is dispatched (pending entries keyed by connId bytes / relay UHID).
// Fixed-capacity arrays with linear scan (Go used maps; the wire behaviour is identical).

#define _POSIX_C_SOURCE 200809L

#include "aethernet/relay_transport.h"
#include "aethernet/circuit_relay.h"

#include <errno.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// ── fixed capacities (linear-scan tables) ───────────────────────────────────
#define RELAY_CAP_RESERVATIONS 64 // relay role: client UHID -> expiry
#define RELAY_CAP_BRIDGES      64 // relay role: connId -> bridge
#define RELAY_CAP_ROUTES       64 // client:     dest -> relay
#define RELAY_CAP_PEER_BRIDGES 64 // endpoint:   peer -> active bridge
#define RELAY_CAP_PENDING      64 // pending CONNECT / RESERVE waiters
#define RELAY_UHID_MAX         128
#define CONN_ID_SIZE           AETHERNET_RELAY_CONN_ID_SIZE

// ── relay role: a bridge this node is relaying ──────────────────────────────
typedef struct {
    bool in_use;
    uint8_t conn_id[CONN_ID_SIZE];
    char a[RELAY_UHID_MAX];
    char b[RELAY_UHID_MAX];
    int64_t data_budget;   // 0 => unlimited
    int64_t deadline_ms;   // 0 => no duration limit
    int64_t data_used;
    bool open;
} relay_bridge_t;

// ── relay role: a granted reservation ───────────────────────────────────────
typedef struct {
    bool in_use;
    char client[RELAY_UHID_MAX];
    int64_t expiry_ms;
} reservation_t;

// ── client: dest -> relay route ─────────────────────────────────────────────
typedef struct {
    bool in_use;
    char dest[RELAY_UHID_MAX];
    char relay[RELAY_UHID_MAX];
} route_t;

// ── endpoint: an established bridge (which connection, via which relay) ──────
typedef struct {
    bool in_use;
    char peer[RELAY_UHID_MAX];
    uint8_t conn_id[CONN_ID_SIZE];
    char relay[RELAY_UHID_MAX];
} peer_bridge_t;

// ── a pending CONNECT (keyed by connId) or RESERVE (keyed by relay) waiter ──
// `signalled` guards against spurious cond wakeups; `status` carries the result.
typedef struct {
    bool in_use;
    bool is_connect;             // true => keyed by conn_id; false => keyed by relay
    uint8_t conn_id[CONN_ID_SIZE];
    char relay[RELAY_UHID_MAX];
    bool signalled;
    uint8_t status;              // AETHERNET_RELAY_STATUS_*
} pending_t;

struct aethernet_relay_transport {
    char local_uhid[RELAY_UHID_MAX];
    aethernet_relay_link_t link;     // copied by value (ctx borrowed)
    aethernet_relay_options_t opts;
    aethernet_relay_now_fn now;
    void *now_ud;

    pthread_mutex_t mu;
    pthread_cond_t  cond;            // signalled when a pending waiter is resolved

    reservation_t reservations[RELAY_CAP_RESERVATIONS];
    relay_bridge_t bridges[RELAY_CAP_BRIDGES];
    route_t routes[RELAY_CAP_ROUTES];
    peer_bridge_t peer_bridges[RELAY_CAP_PEER_BRIDGES];
    pending_t pending[RELAY_CAP_PENDING];

    aethernet_relay_on_data_fn on_data;
    void *on_data_ud;

    bool destroyed;
};

// ── clock ───────────────────────────────────────────────────────────────────

static int64_t wallclock_now_ms(void *ud) {
    (void)ud;
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static int64_t now_ms(aethernet_relay_transport_t *t) {
    return t->now ? t->now(t->now_ud) : wallclock_now_ms(NULL);
}

aethernet_relay_options_t aethernet_relay_options_default(void) {
    aethernet_relay_options_t o;
    o.reservation_ttl_ms = 30LL * 60LL * 1000LL; // 30 minutes
    o.max_reservations = 128;
    o.max_bridges = 128;
    o.bridge_data_limit_bytes = 0;
    o.bridge_duration_limit_seconds = 0;
    o.connect_timeout_ms = 10000;
    o.reserve_timeout_ms = 10000;
    o.act_as_relay = true;
    return o;
}

// ── small helpers ───────────────────────────────────────────────────────────

static void copy_uhid(char *dst, const char *src) {
    if (!src) { dst[0] = '\0'; return; }
    size_t n = strlen(src);
    if (n >= RELAY_UHID_MAX) n = RELAY_UHID_MAX - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static bool str_eq(const char *a, const char *b) {
    return strcmp(a ? a : "", b ? b : "") == 0;
}

// 16 random bytes for a fresh connection id (uniqueness is all the engine needs;
// wire format is opaque bytes, not a parsed UUID).
static void gen_conn_id(uint8_t out[CONN_ID_SIZE]) {
    for (int i = 0; i < CONN_ID_SIZE; i++) out[i] = (uint8_t)(rand() & 0xff);
}

// ── link plumbing ───────────────────────────────────────────────────────────

static bool link_send(aethernet_relay_transport_t *t, const char *to, const uint8_t *frame, uint32_t len) {
    return t->link.send_frame(t->link.ctx, to, frame, len);
}
static bool link_can_reach(aethernet_relay_transport_t *t, const char *node) {
    return t->link.can_reach(t->link.ctx, node);
}

// Serialize `f` and hand it one hop to `to`. Returns whether the link accepted it.
// Never takes the mutex — safe to call while unlocked (like Go's t.send).
static bool send_frame(aethernet_relay_transport_t *t, const char *to, const aethernet_relay_frame_t *f) {
    uint8_t *buf = NULL;
    uint32_t buf_len = 0;
    if (!aethernet_relay_frame_encode(f, &buf, &buf_len)) return false;
    bool ok = link_send(t, to, buf, buf_len);
    free(buf);
    return ok;
}

// Build a frame with everything zeroed (nil conn id, empty strings) so callers only
// set the fields they use — mirrors the Go/C# struct-literal style.
static aethernet_relay_frame_t blank_frame(void) {
    aethernet_relay_frame_t f;
    memset(&f, 0, sizeof f);
    return f;
}

// ── table lookups (all called under t->mu) ──────────────────────────────────

static reservation_t *find_reservation(aethernet_relay_transport_t *t, const char *client) {
    for (int i = 0; i < RELAY_CAP_RESERVATIONS; i++)
        if (t->reservations[i].in_use && str_eq(t->reservations[i].client, client))
            return &t->reservations[i];
    return NULL;
}
static int count_reservations(aethernet_relay_transport_t *t) {
    int n = 0;
    for (int i = 0; i < RELAY_CAP_RESERVATIONS; i++) if (t->reservations[i].in_use) n++;
    return n;
}

static relay_bridge_t *find_bridge(aethernet_relay_transport_t *t, const uint8_t conn_id[CONN_ID_SIZE]) {
    for (int i = 0; i < RELAY_CAP_BRIDGES; i++)
        if (t->bridges[i].in_use && memcmp(t->bridges[i].conn_id, conn_id, CONN_ID_SIZE) == 0)
            return &t->bridges[i];
    return NULL;
}
static int count_bridges(aethernet_relay_transport_t *t) {
    int n = 0;
    for (int i = 0; i < RELAY_CAP_BRIDGES; i++) if (t->bridges[i].in_use) n++;
    return n;
}

static peer_bridge_t *find_peer_bridge(aethernet_relay_transport_t *t, const char *peer) {
    for (int i = 0; i < RELAY_CAP_PEER_BRIDGES; i++)
        if (t->peer_bridges[i].in_use && str_eq(t->peer_bridges[i].peer, peer))
            return &t->peer_bridges[i];
    return NULL;
}

// Insert or overwrite the endpoint's bridge record for `peer`.
static void put_peer_bridge(aethernet_relay_transport_t *t, const char *peer,
                            const uint8_t conn_id[CONN_ID_SIZE], const char *relay) {
    peer_bridge_t *pb = find_peer_bridge(t, peer);
    if (!pb) {
        for (int i = 0; i < RELAY_CAP_PEER_BRIDGES; i++)
            if (!t->peer_bridges[i].in_use) { pb = &t->peer_bridges[i]; break; }
    }
    if (!pb) return; // table full — drop (fixed-capacity, like the Go map would just grow)
    pb->in_use = true;
    copy_uhid(pb->peer, peer);
    memcpy(pb->conn_id, conn_id, CONN_ID_SIZE);
    copy_uhid(pb->relay, relay);
}

// ── pending-waiter management (all under t->mu) ─────────────────────────────

static pending_t *pending_alloc(aethernet_relay_transport_t *t) {
    for (int i = 0; i < RELAY_CAP_PENDING; i++)
        if (!t->pending[i].in_use) { memset(&t->pending[i], 0, sizeof(pending_t)); return &t->pending[i]; }
    return NULL;
}

// Resolve a pending CONNECT waiter (keyed by conn id) if one exists.
static void resolve_connect(aethernet_relay_transport_t *t, const uint8_t conn_id[CONN_ID_SIZE], uint8_t status) {
    for (int i = 0; i < RELAY_CAP_PENDING; i++) {
        pending_t *p = &t->pending[i];
        if (p->in_use && p->is_connect && !p->signalled &&
            memcmp(p->conn_id, conn_id, CONN_ID_SIZE) == 0) {
            p->status = status;
            p->signalled = true;
            pthread_cond_broadcast(&t->cond);
            return;
        }
    }
}

// Resolve a pending RESERVE waiter (keyed by relay UHID) if one exists.
static void resolve_reservation(aethernet_relay_transport_t *t, const char *relay, uint8_t status) {
    for (int i = 0; i < RELAY_CAP_PENDING; i++) {
        pending_t *p = &t->pending[i];
        if (p->in_use && !p->is_connect && !p->signalled && str_eq(p->relay, relay)) {
            p->status = status;
            p->signalled = true;
            pthread_cond_broadcast(&t->cond);
            return;
        }
    }
}

// Absolute-deadline timedwait on t->cond until *p is signalled or timeout elapses.
// Returns the resolved status, or CONNECTION_FAILED on timeout. Called with t->mu held.
static uint8_t pending_await(aethernet_relay_transport_t *t, pending_t *p, int64_t timeout_ms) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_ms / 1000;
    deadline.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) { deadline.tv_sec += 1; deadline.tv_nsec -= 1000000000L; }

    while (!p->signalled && !t->destroyed) {
        int rc = pthread_cond_timedwait(&t->cond, &t->mu, &deadline);
        if (rc == ETIMEDOUT) break;
    }
    return p->signalled ? p->status : AETHERNET_RELAY_STATUS_CONNECTION_FAILED;
}

static void pending_free(pending_t *p) { p->in_use = false; }

// ── lifecycle ───────────────────────────────────────────────────────────────

aethernet_relay_transport_t *aethernet_relay_transport_new(
    const char *local_uhid, const aethernet_relay_link_t *link,
    aethernet_relay_options_t options, aethernet_relay_now_fn now, void *now_ud) {
    if (!local_uhid || !link || !link->send_frame || !link->can_reach) return NULL;

    aethernet_relay_transport_t *t = (aethernet_relay_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;

    copy_uhid(t->local_uhid, local_uhid);
    t->link = *link;
    t->opts = options;
    t->now = now;
    t->now_ud = now_ud;
    t->on_data = NULL;
    t->on_data_ud = NULL;
    t->destroyed = false;

    if (pthread_mutex_init(&t->mu, NULL) != 0) { free(t); return NULL; }
    if (pthread_cond_init(&t->cond, NULL) != 0) { pthread_mutex_destroy(&t->mu); free(t); return NULL; }
    return t;
}

void aethernet_relay_transport_destroy(aethernet_relay_transport_t *t) {
    if (!t) return;
    pthread_mutex_lock(&t->mu);
    t->destroyed = true;
    // Fail every pending waiter so no thread is left blocked (mirrors C# Dispose()).
    for (int i = 0; i < RELAY_CAP_PENDING; i++) {
        if (t->pending[i].in_use && !t->pending[i].signalled) {
            t->pending[i].status = AETHERNET_RELAY_STATUS_CONNECTION_FAILED;
            t->pending[i].signalled = true;
        }
    }
    pthread_cond_broadcast(&t->cond);
    pthread_mutex_unlock(&t->mu);

    pthread_mutex_destroy(&t->mu);
    pthread_cond_destroy(&t->cond);
    free(t);
}

void aethernet_relay_transport_set_on_data(aethernet_relay_transport_t *t,
                                           aethernet_relay_on_data_fn cb, void *user_data) {
    pthread_mutex_lock(&t->mu);
    t->on_data = cb;
    t->on_data_ud = user_data;
    pthread_mutex_unlock(&t->mu);
}

bool aethernet_relay_transport_set_route(aethernet_relay_transport_t *t,
                                         const char *dest, const char *relay) {
    pthread_mutex_lock(&t->mu);
    route_t *slot = NULL;
    for (int i = 0; i < RELAY_CAP_ROUTES; i++) {
        if (t->routes[i].in_use && str_eq(t->routes[i].dest, dest)) { slot = &t->routes[i]; break; }
    }
    if (!slot) {
        for (int i = 0; i < RELAY_CAP_ROUTES; i++)
            if (!t->routes[i].in_use) { slot = &t->routes[i]; break; }
    }
    bool ok = slot != NULL;
    if (ok) {
        slot->in_use = true;
        copy_uhid(slot->dest, dest);
        copy_uhid(slot->relay, relay);
    }
    pthread_mutex_unlock(&t->mu);
    return ok;
}

// ── diagnostics ─────────────────────────────────────────────────────────────

int aethernet_relay_transport_active_bridge_count(aethernet_relay_transport_t *t) {
    pthread_mutex_lock(&t->mu);
    int n = count_bridges(t);
    pthread_mutex_unlock(&t->mu);
    return n;
}
int aethernet_relay_transport_active_reservation_count(aethernet_relay_transport_t *t) {
    pthread_mutex_lock(&t->mu);
    int n = count_reservations(t);
    pthread_mutex_unlock(&t->mu);
    return n;
}
bool aethernet_relay_transport_is_connected(aethernet_relay_transport_t *t, const char *peer) {
    pthread_mutex_lock(&t->mu);
    bool ok = find_peer_bridge(t, peer) != NULL;
    pthread_mutex_unlock(&t->mu);
    return ok;
}

// ── target: Reserve ─────────────────────────────────────────────────────────

bool aethernet_relay_transport_reserve(aethernet_relay_transport_t *t, const char *relay) {
    if (!link_can_reach(t, relay)) return false;

    pthread_mutex_lock(&t->mu);
    pending_t *p = pending_alloc(t);
    if (!p) { pthread_mutex_unlock(&t->mu); return false; }
    p->in_use = true;
    p->is_connect = false;
    copy_uhid(p->relay, relay);
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t f = blank_frame();
    f.type = AETHERNET_RELAY_RESERVE;
    f.source_uhid = t->local_uhid;
    f.relay_uhid = (char *)relay;
    send_frame(t, relay, &f); // fire the RESERVE; response arrives via on_frame

    pthread_mutex_lock(&t->mu);
    uint8_t status = pending_await(t, p, t->opts.reserve_timeout_ms);
    pending_free(p);
    pthread_mutex_unlock(&t->mu);
    return status == AETHERNET_RELAY_STATUS_OK;
}

// ── client: connect handshake ───────────────────────────────────────────────

// Returns the negotiated status. On OK the endpoint's peer-bridge is already
// recorded by handle_connect_response before this returns.
static uint8_t do_connect(aethernet_relay_transport_t *t, const char *dest, const char *relay) {
    uint8_t conn_id[CONN_ID_SIZE];
    gen_conn_id(conn_id);

    pthread_mutex_lock(&t->mu);
    pending_t *p = pending_alloc(t);
    if (!p) { pthread_mutex_unlock(&t->mu); return AETHERNET_RELAY_STATUS_CONNECTION_FAILED; }
    p->in_use = true;
    p->is_connect = true;
    memcpy(p->conn_id, conn_id, CONN_ID_SIZE);
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t f = blank_frame();
    f.type = AETHERNET_RELAY_CONNECT;
    f.source_uhid = t->local_uhid;
    f.destination_uhid = (char *)dest;
    f.relay_uhid = (char *)relay;
    memcpy(f.connection_id, conn_id, CONN_ID_SIZE);

    if (!send_frame(t, relay, &f)) {
        pthread_mutex_lock(&t->mu);
        pending_free(p);
        pthread_mutex_unlock(&t->mu);
        return AETHERNET_RELAY_STATUS_CONNECTION_FAILED;
    }

    pthread_mutex_lock(&t->mu);
    uint8_t status = pending_await(t, p, t->opts.connect_timeout_ms);
    pending_free(p);
    pthread_mutex_unlock(&t->mu);
    return status;
}

// Send a tunnelled DATA frame over an established bridge. `relay`/`conn_id` come from
// the endpoint's peer-bridge record (snapshotted under the lock by the caller).
static bool send_data(aethernet_relay_transport_t *t, const char *peer,
                      const uint8_t conn_id[CONN_ID_SIZE], const char *relay,
                      const uint8_t *data, uint32_t len) {
    aethernet_relay_frame_t f = blank_frame();
    f.type = AETHERNET_RELAY_DATA;
    f.source_uhid = t->local_uhid;
    f.destination_uhid = (char *)peer;
    f.relay_uhid = (char *)relay;
    memcpy(f.connection_id, conn_id, CONN_ID_SIZE);
    f.payload = (uint8_t *)data;
    f.payload_len = len;
    return send_frame(t, relay, &f);
}

bool aethernet_relay_transport_send(aethernet_relay_transport_t *t,
                                    const char *peer, const uint8_t *data, uint32_t len) {
    // Fast path: bridge already established for this peer.
    pthread_mutex_lock(&t->mu);
    peer_bridge_t *pb = find_peer_bridge(t, peer);
    if (pb) {
        uint8_t conn_id[CONN_ID_SIZE];
        char relay[RELAY_UHID_MAX];
        memcpy(conn_id, pb->conn_id, CONN_ID_SIZE);
        copy_uhid(relay, pb->relay);
        pthread_mutex_unlock(&t->mu);
        return send_data(t, peer, conn_id, relay, data, len);
    }
    // Need a route to a reachable relay.
    char relay[RELAY_UHID_MAX];
    relay[0] = '\0';
    for (int i = 0; i < RELAY_CAP_ROUTES; i++) {
        if (t->routes[i].in_use && str_eq(t->routes[i].dest, peer)) { copy_uhid(relay, t->routes[i].relay); break; }
    }
    pthread_mutex_unlock(&t->mu);

    if (relay[0] == '\0' || !link_can_reach(t, relay)) return false;
    if (do_connect(t, peer, relay) != AETHERNET_RELAY_STATUS_OK) return false;

    // Bridge just recorded by handle_connect_response — re-snapshot and send.
    pthread_mutex_lock(&t->mu);
    pb = find_peer_bridge(t, peer);
    if (!pb) { pthread_mutex_unlock(&t->mu); return false; }
    uint8_t conn_id[CONN_ID_SIZE];
    char relay2[RELAY_UHID_MAX];
    memcpy(conn_id, pb->conn_id, CONN_ID_SIZE);
    copy_uhid(relay2, pb->relay);
    pthread_mutex_unlock(&t->mu);
    return send_data(t, peer, conn_id, relay2, data, len);
}

// ── inbound handlers (each mirrors the Go handleX) ──────────────────────────

// Relay: reply to a CONNECT with a ConnectResponse carrying `status`.
static void reply_connect(aethernet_relay_transport_t *t, const char *client,
                          const aethernet_relay_frame_t *connect, uint8_t status) {
    aethernet_relay_frame_t r = blank_frame();
    r.type = AETHERNET_RELAY_CONNECT_RESPONSE;
    r.source_uhid = connect->source_uhid;
    r.destination_uhid = connect->destination_uhid;
    r.relay_uhid = t->local_uhid;
    memcpy(r.connection_id, connect->connection_id, CONN_ID_SIZE);
    r.status = status;
    send_frame(t, client, &r);
}

// Relay: grant/refuse a reservation.
static void handle_reserve(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    pthread_mutex_lock(&t->mu);
    if (!t->opts.act_as_relay || count_reservations(t) >= t->opts.max_reservations) {
        pthread_mutex_unlock(&t->mu);
        aethernet_relay_frame_t r = blank_frame();
        r.type = AETHERNET_RELAY_RESERVE_RESPONSE;
        r.source_uhid = f->source_uhid;
        r.relay_uhid = t->local_uhid;
        r.status = AETHERNET_RELAY_STATUS_RESERVATION_REFUSED;
        send_frame(t, from, &r);
        return;
    }
    int64_t expiry = now_ms(t) + t->opts.reservation_ttl_ms;
    reservation_t *res = find_reservation(t, f->source_uhid);
    if (!res) {
        for (int i = 0; i < RELAY_CAP_RESERVATIONS; i++)
            if (!t->reservations[i].in_use) { res = &t->reservations[i]; break; }
    }
    if (res) {
        res->in_use = true;
        copy_uhid(res->client, f->source_uhid);
        res->expiry_ms = expiry;
    }
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t r = blank_frame();
    r.type = AETHERNET_RELAY_RESERVE_RESPONSE;
    r.source_uhid = f->source_uhid;
    r.relay_uhid = t->local_uhid;
    r.status = AETHERNET_RELAY_STATUS_OK;
    r.reservation_expires_at_ms = expiry;
    send_frame(t, from, &r);
}

// Client: reservation confirmed/denied — wake the RESERVE waiter keyed by `from`.
static void handle_reserve_response(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    pthread_mutex_lock(&t->mu);
    resolve_reservation(t, from, f->status);
    pthread_mutex_unlock(&t->mu);
}

// Relay: A wants B. Validate B's reservation + reachability, open a STOP to B.
static void handle_connect(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    (void)from;
    const char *a = f->source_uhid;
    const char *b = f->destination_uhid;

    if (!t->opts.act_as_relay) { reply_connect(t, a, f, AETHERNET_RELAY_STATUS_CONNECTION_FAILED); return; }

    pthread_mutex_lock(&t->mu);
    reservation_t *res = find_reservation(t, b);
    if (!res || now_ms(t) >= res->expiry_ms) {
        if (res) res->in_use = false; // expired reservation is dropped (like Go's delete)
        pthread_mutex_unlock(&t->mu);
        reply_connect(t, a, f, AETHERNET_RELAY_STATUS_NO_RESERVATION);
        return;
    }
    if (!link_can_reach(t, b)) {
        pthread_mutex_unlock(&t->mu);
        reply_connect(t, a, f, AETHERNET_RELAY_STATUS_CONNECTION_FAILED);
        return;
    }
    if (count_bridges(t) >= t->opts.max_bridges) {
        pthread_mutex_unlock(&t->mu);
        reply_connect(t, a, f, AETHERNET_RELAY_STATUS_RESOURCE_LIMIT_EXCEEDED);
        return;
    }
    relay_bridge_t *br = NULL;
    for (int i = 0; i < RELAY_CAP_BRIDGES; i++)
        if (!t->bridges[i].in_use) { br = &t->bridges[i]; break; }
    if (!br) {
        pthread_mutex_unlock(&t->mu);
        reply_connect(t, a, f, AETHERNET_RELAY_STATUS_RESOURCE_LIMIT_EXCEEDED);
        return;
    }
    memset(br, 0, sizeof(*br));
    br->in_use = true;
    memcpy(br->conn_id, f->connection_id, CONN_ID_SIZE);
    copy_uhid(br->a, a);
    copy_uhid(br->b, b);
    br->data_budget = t->opts.bridge_data_limit_bytes;
    br->deadline_ms = (t->opts.bridge_duration_limit_seconds > 0)
        ? now_ms(t) + (int64_t)t->opts.bridge_duration_limit_seconds * 1000
        : 0;
    br->open = false;
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t stop = blank_frame();
    stop.type = AETHERNET_RELAY_STOP;
    stop.source_uhid = (char *)a;
    stop.destination_uhid = (char *)b;
    stop.relay_uhid = t->local_uhid;
    memcpy(stop.connection_id, f->connection_id, CONN_ID_SIZE);
    stop.limit_data_bytes = t->opts.bridge_data_limit_bytes;
    stop.limit_duration_seconds = t->opts.bridge_duration_limit_seconds;
    send_frame(t, b, &stop);
}

// Target: relay says A wants us. Accept and record a return route to A via `from`.
static void handle_stop(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    pthread_mutex_lock(&t->mu);
    put_peer_bridge(t, f->source_uhid, f->connection_id, from);
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t r = blank_frame();
    r.type = AETHERNET_RELAY_STOP_RESPONSE;
    r.source_uhid = f->source_uhid;
    r.destination_uhid = t->local_uhid;
    r.relay_uhid = (char *)from;
    memcpy(r.connection_id, f->connection_id, CONN_ID_SIZE);
    r.status = AETHERNET_RELAY_STATUS_OK;
    send_frame(t, from, &r);
}

// Relay: target accepted/refused. Finalise the bridge and answer the client A.
static void handle_stop_response(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    (void)from;
    pthread_mutex_lock(&t->mu);
    relay_bridge_t *br = find_bridge(t, f->connection_id);
    if (!br) { pthread_mutex_unlock(&t->mu); return; }

    if (f->status != AETHERNET_RELAY_STATUS_OK) {
        char a[RELAY_UHID_MAX];
        copy_uhid(a, br->a);
        br->in_use = false;
        pthread_mutex_unlock(&t->mu);
        reply_connect(t, a, f, AETHERNET_RELAY_STATUS_CONNECTION_FAILED);
        return;
    }
    br->open = true;
    char a[RELAY_UHID_MAX], b[RELAY_UHID_MAX];
    copy_uhid(a, br->a);
    copy_uhid(b, br->b);
    int64_t budget = br->data_budget;
    pthread_mutex_unlock(&t->mu);

    aethernet_relay_frame_t ok = blank_frame();
    ok.type = AETHERNET_RELAY_CONNECT_RESPONSE;
    ok.source_uhid = a;
    ok.destination_uhid = b;
    ok.relay_uhid = t->local_uhid;
    memcpy(ok.connection_id, f->connection_id, CONN_ID_SIZE);
    ok.status = AETHERNET_RELAY_STATUS_OK;
    ok.limit_data_bytes = budget;
    send_frame(t, a, &ok);
}

// Client: bridge established/refused — record the peer-bridge on OK, wake the waiter.
static void handle_connect_response(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    pthread_mutex_lock(&t->mu);
    if (f->status == AETHERNET_RELAY_STATUS_OK) {
        put_peer_bridge(t, f->destination_uhid, f->connection_id, from);
    }
    resolve_connect(t, f->connection_id, f->status);
    pthread_mutex_unlock(&t->mu);
}

// Data: endpoint delivery (dst == us) or relay forward to the other leg under budget.
static void handle_data(aethernet_relay_transport_t *t, const char *from, const aethernet_relay_frame_t *f) {
    if (str_eq(f->destination_uhid, t->local_uhid)) {
        pthread_mutex_lock(&t->mu);
        aethernet_relay_on_data_fn cb = t->on_data;
        void *ud = t->on_data_ud;
        pthread_mutex_unlock(&t->mu);
        if (cb) cb(f->source_uhid, f->payload, f->payload_len, ud);
        return;
    }

    pthread_mutex_lock(&t->mu);
    relay_bridge_t *br = find_bridge(t, f->connection_id);
    if (!br || !br->open || (!str_eq(from, br->a) && !str_eq(from, br->b))) {
        pthread_mutex_unlock(&t->mu);
        return;
    }
    if (br->deadline_ms != 0 && now_ms(t) >= br->deadline_ms) {
        br->in_use = false; // duration breach — tear the bridge down
        pthread_mutex_unlock(&t->mu);
        return;
    }
    br->data_used += (int64_t)f->payload_len;
    if (br->data_budget > 0 && br->data_used > br->data_budget) {
        br->in_use = false; // data-budget breach — tear the bridge down
        pthread_mutex_unlock(&t->mu);
        return;
    }
    pthread_mutex_unlock(&t->mu);

    // Forward the frame unchanged to the other endpoint (= its dst).
    send_frame(t, f->destination_uhid, f);
}

// ── inbound entry point ─────────────────────────────────────────────────────

void aethernet_relay_transport_on_frame(aethernet_relay_transport_t *t,
                                        const char *from, const uint8_t *frame, uint32_t len) {
    if (!t) return;
    aethernet_relay_frame_t *f = aethernet_relay_frame_decode(frame, len);
    if (!f) return; // drop malformed

    switch (f->type) {
        case AETHERNET_RELAY_RESERVE:          handle_reserve(t, from, f); break;
        case AETHERNET_RELAY_RESERVE_RESPONSE: handle_reserve_response(t, from, f); break;
        case AETHERNET_RELAY_CONNECT:          handle_connect(t, from, f); break;
        case AETHERNET_RELAY_STOP:             handle_stop(t, from, f); break;
        case AETHERNET_RELAY_STOP_RESPONSE:    handle_stop_response(t, from, f); break;
        case AETHERNET_RELAY_CONNECT_RESPONSE: handle_connect_response(t, from, f); break;
        case AETHERNET_RELAY_DATA:             handle_data(t, from, f); break;
        default: break;
    }
    aethernet_relay_frame_free(f);
}
