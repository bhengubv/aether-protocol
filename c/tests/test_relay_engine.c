// SPDX-License-Identifier: MIT
// Behavioural proof of the native circuit-relay-v2 ENGINE (c/src/relay_transport.c).
// A three-node topology where A and B can each reach relay R but NOT each other
// directly: a message from A must traverse the relay bridge to reach B — no server,
// no libp2p. Mirrors go/circuitrelay/transport_test.go's 6 cases one-for-one, using
// an in-process one-hop mesh whose deliver() spawns a DETACHED pthread to call the
// target's on_frame (async hop, like Go's `go func`). Received messages land in a
// thread-safe collector waited on with a timeout.

#define _POSIX_C_SOURCE 200809L

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/relay_transport.h"

// ── test runner ─────────────────────────────────────────────────────────────

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

// ── in-process one-hop mesh ─────────────────────────────────────────────────
//
// Nodes register a transport by name; connect(x,y) makes an undirected edge. A link
// SendFrame delivers a frame to the target's on_frame on a detached thread, but only
// across an existing edge — exactly the Go mesh semantics.

#define MESH_MAX_NODES 8
#define MESH_MAX_EDGES 32

typedef struct {
    char name[64];
    aethernet_relay_transport_t *transport;
} mesh_node_t;

typedef struct {
    char x[64];
    char y[64];
} mesh_edge_t;

typedef struct {
    pthread_mutex_t mu;
    mesh_node_t nodes[MESH_MAX_NODES];
    int node_count;
    mesh_edge_t edges[MESH_MAX_EDGES];
    int edge_count;
} mesh_t;

// A per-transport link context: which mesh, and this node's own name (the `from`).
typedef struct {
    mesh_t *mesh;
    char node[64];
} link_ctx_t;

static void mesh_init(mesh_t *m) {
    memset(m, 0, sizeof(*m));
    pthread_mutex_init(&m->mu, NULL);
}
static void mesh_destroy(mesh_t *m) { pthread_mutex_destroy(&m->mu); }

static void mesh_connect(mesh_t *m, const char *x, const char *y) {
    pthread_mutex_lock(&m->mu);
    mesh_edge_t *e = &m->edges[m->edge_count++];
    snprintf(e->x, sizeof e->x, "%s", x);
    snprintf(e->y, sizeof e->y, "%s", y);
    pthread_mutex_unlock(&m->mu);
}

static bool mesh_adjacent(mesh_t *m, const char *x, const char *y) {
    pthread_mutex_lock(&m->mu);
    bool adj = false;
    for (int i = 0; i < m->edge_count; i++) {
        mesh_edge_t *e = &m->edges[i];
        if ((strcmp(e->x, x) == 0 && strcmp(e->y, y) == 0) ||
            (strcmp(e->x, y) == 0 && strcmp(e->y, x) == 0)) { adj = true; break; }
    }
    pthread_mutex_unlock(&m->mu);
    return adj;
}

static void mesh_register(mesh_t *m, const char *name, aethernet_relay_transport_t *tr) {
    pthread_mutex_lock(&m->mu);
    mesh_node_t *n = &m->nodes[m->node_count++];
    snprintf(n->name, sizeof n->name, "%s", name);
    n->transport = tr;
    pthread_mutex_unlock(&m->mu);
}

static aethernet_relay_transport_t *mesh_transport(mesh_t *m, const char *name) {
    pthread_mutex_lock(&m->mu);
    aethernet_relay_transport_t *tr = NULL;
    for (int i = 0; i < m->node_count; i++)
        if (strcmp(m->nodes[i].name, name) == 0) { tr = m->nodes[i].transport; break; }
    pthread_mutex_unlock(&m->mu);
    return tr;
}

// A single async hop: owns a heap copy of the frame + endpoints; frees them after
// invoking the target's on_frame. Runs on a detached pthread.
typedef struct {
    mesh_t *mesh;
    char from[64];
    char to[64];
    uint8_t *frame;
    uint32_t len;
} hop_t;

static void *hop_run(void *arg) {
    hop_t *h = (hop_t *)arg;
    aethernet_relay_transport_t *tr = mesh_transport(h->mesh, h->to);
    if (tr) aethernet_relay_transport_on_frame(tr, h->from, h->frame, h->len);
    free(h->frame);
    free(h);
    return NULL;
}

// ── RelayLink vtable impl over the mesh ─────────────────────────────────────

static bool link_send_frame(void *ctx, const char *node, const uint8_t *frame, uint32_t len) {
    link_ctx_t *lc = (link_ctx_t *)ctx;
    if (!mesh_adjacent(lc->mesh, lc->node, node)) return false;

    hop_t *h = (hop_t *)calloc(1, sizeof(hop_t));
    if (!h) return false;
    h->mesh = lc->mesh;
    snprintf(h->from, sizeof h->from, "%s", lc->node);
    snprintf(h->to, sizeof h->to, "%s", node);
    h->frame = (uint8_t *)malloc(len ? len : 1);
    if (!h->frame) { free(h); return false; }
    if (len) memcpy(h->frame, frame, len);
    h->len = len;

    pthread_t th;
    if (pthread_create(&th, NULL, hop_run, h) != 0) { free(h->frame); free(h); return false; }
    pthread_detach(th);
    return true;
}

static bool link_can_reach(void *ctx, const char *node) {
    link_ctx_t *lc = (link_ctx_t *)ctx;
    return mesh_adjacent(lc->mesh, lc->node, node);
}

// ── thread-safe receive collector ───────────────────────────────────────────

typedef struct {
    char sender[128];
    char data[512];
    uint32_t len;
} recv_t;

typedef struct {
    pthread_mutex_t mu;
    pthread_cond_t  cond;
    recv_t items[16];
    int count;
} collector_t;

static void collector_init(collector_t *c) {
    memset(c, 0, sizeof(*c));
    pthread_mutex_init(&c->mu, NULL);
    pthread_cond_init(&c->cond, NULL);
}
static void collector_destroy(collector_t *c) {
    pthread_mutex_destroy(&c->mu);
    pthread_cond_destroy(&c->cond);
}

static void collector_on_data(const char *sender, const uint8_t *data, uint32_t len, void *ud) {
    collector_t *c = (collector_t *)ud;
    pthread_mutex_lock(&c->mu);
    if (c->count < (int)(sizeof c->items / sizeof c->items[0])) {
        recv_t *r = &c->items[c->count++];
        snprintf(r->sender, sizeof r->sender, "%s", sender ? sender : "");
        uint32_t n = len < sizeof(r->data) ? len : (uint32_t)(sizeof(r->data) - 1);
        if (n) memcpy(r->data, data, n);
        r->data[n] = '\0';
        r->len = len;
    }
    pthread_cond_broadcast(&c->cond);
    pthread_mutex_unlock(&c->mu);
}

// Wait until at least `want` items have arrived or `timeout_ms` elapses.
// Returns true if the count was reached. Copies the item at index `want-1` into *out.
static bool collector_wait(collector_t *c, int want, int64_t timeout_ms, recv_t *out) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_ms / 1000;
    deadline.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) { deadline.tv_sec += 1; deadline.tv_nsec -= 1000000000L; }

    pthread_mutex_lock(&c->mu);
    while (c->count < want) {
        int rc = pthread_cond_timedwait(&c->cond, &c->mu, &deadline);
        if (rc != 0) break;
    }
    bool ok = c->count >= want;
    if (ok && out) *out = c->items[want - 1];
    pthread_mutex_unlock(&c->mu);
    return ok;
}

// Assert that NO further item arrives within timeout_ms beyond `already` (a negative
// proof, used for the drop / refuse / expiry cases).
static bool collector_expect_none(collector_t *c, int already, int64_t timeout_ms) {
    recv_t tmp;
    return !collector_wait(c, already + 1, timeout_ms, &tmp);
}

// ── controllable clock (injectable, mirrors Go's testClock) ─────────────────

typedef struct {
    pthread_mutex_t mu;
    int64_t t_ms;
} test_clock_t;

static test_clock_t g_clock; // single relay-side clock is enough for the expiry test

static void clock_init(test_clock_t *c) {
    pthread_mutex_init(&c->mu, NULL);
    // 2026-01-01T00:00:00Z in unix ms (matches the Go testClock epoch spirit).
    c->t_ms = 1767225600000LL;
}
static int64_t clock_now(void *ud) {
    test_clock_t *c = (test_clock_t *)ud;
    pthread_mutex_lock(&c->mu);
    int64_t v = c->t_ms;
    pthread_mutex_unlock(&c->mu);
    return v;
}
static void clock_advance(test_clock_t *c, int64_t d_ms) {
    pthread_mutex_lock(&c->mu);
    c->t_ms += d_ms;
    pthread_mutex_unlock(&c->mu);
}

// ── line topology builder: A ── R ── B with NO A-B edge ─────────────────────

typedef struct {
    mesh_t mesh;
    link_ctx_t ctx_a, ctx_r, ctx_b;
    aethernet_relay_link_t link_a, link_r, link_b;
    aethernet_relay_transport_t *a, *r, *b;
    collector_t a_recv, b_recv;
} line_t;

static void make_link(aethernet_relay_link_t *link, link_ctx_t *ctx, mesh_t *m, const char *node) {
    ctx->mesh = m;
    snprintf(ctx->node, sizeof ctx->node, "%s", node);
    link->ctx = ctx;
    link->send_frame = link_send_frame;
    link->can_reach = link_can_reach;
}

// relay_opts/relay_now configure R; A and B use defaults with wall clock.
static void line_build(line_t *L, aethernet_relay_options_t relay_opts,
                       aethernet_relay_now_fn relay_now, void *relay_now_ud) {
    mesh_init(&L->mesh);
    mesh_connect(&L->mesh, "A", "R");
    mesh_connect(&L->mesh, "R", "B");

    make_link(&L->link_a, &L->ctx_a, &L->mesh, "A");
    make_link(&L->link_r, &L->ctx_r, &L->mesh, "R");
    make_link(&L->link_b, &L->ctx_b, &L->mesh, "B");

    L->a = aethernet_relay_transport_new("A", &L->link_a, aethernet_relay_options_default(), NULL, NULL);
    L->r = aethernet_relay_transport_new("R", &L->link_r, relay_opts, relay_now, relay_now_ud);
    L->b = aethernet_relay_transport_new("B", &L->link_b, aethernet_relay_options_default(), NULL, NULL);
    if (!L->a || !L->r || !L->b) FAILF("transport_new returned NULL");

    // Register transports so link SendFrame can locate the target's on_frame.
    mesh_register(&L->mesh, "A", L->a);
    mesh_register(&L->mesh, "R", L->r);
    mesh_register(&L->mesh, "B", L->b);

    collector_init(&L->a_recv);
    collector_init(&L->b_recv);
    aethernet_relay_transport_set_on_data(L->a, collector_on_data, &L->a_recv);
    aethernet_relay_transport_set_on_data(L->b, collector_on_data, &L->b_recv);
}

static void line_teardown(line_t *L) {
    aethernet_relay_transport_destroy(L->a);
    aethernet_relay_transport_destroy(L->r);
    aethernet_relay_transport_destroy(L->b);
    collector_destroy(&L->a_recv);
    collector_destroy(&L->b_recv);
    mesh_destroy(&L->mesh);
}

// ── the 6 behavioural cases (mirror transport_test.go) ──────────────────────

// (a) A→R→B relay: B receives {A, "deadbeef"}; relay active bridge count == 1.
static void test_message_traverses_relay_no_direct_link(void) {
    line_t L;
    line_build(&L, aethernet_relay_options_default(), NULL, NULL);

    if (aethernet_relay_transport_is_connected(L.a, "B")) FAILF("A should not be directly connected to B");
    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("B.Reserve(R) failed");
    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("set_route failed");

    if (!aethernet_relay_transport_send(L.a, "B", (const uint8_t *)"deadbeef", 8)) FAILF("A.Send returned false");

    recv_t got;
    if (!collector_wait(&L.b_recv, 1, 3000, &got)) FAILF("timeout waiting for B to receive relayed message");
    if (strcmp(got.sender, "A") != 0 || strcmp(got.data, "deadbeef") != 0)
        FAILF("B got {%s %s}, want {A deadbeef}", got.sender, got.data);
    if (aethernet_relay_transport_active_bridge_count(L.r) != 1)
        FAILF("relay bridge count = %d, want 1", aethernet_relay_transport_active_bridge_count(L.r));

    line_teardown(&L);
}

// (b) bidirectional: after A→B, B can reply A→ gets {B, "reply"}.
static void test_bridge_is_bidirectional(void) {
    line_t L;
    line_build(&L, aethernet_relay_options_default(), NULL, NULL);

    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("reserve failed");
    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("set_route failed");
    if (!aethernet_relay_transport_send(L.a, "B", (const uint8_t *)"hi", 2)) FAILF("A.Send failed");
    if (!collector_wait(&L.b_recv, 1, 3000, NULL)) FAILF("timeout waiting for B to receive");

    if (!aethernet_relay_transport_send(L.b, "A", (const uint8_t *)"reply", 5)) FAILF("B.Send(A) failed");
    recv_t got;
    if (!collector_wait(&L.a_recv, 1, 3000, &got)) FAILF("timeout waiting for A to receive B's reply");
    if (strcmp(got.sender, "B") != 0 || strcmp(got.data, "reply") != 0)
        FAILF("A got {%s %s}, want {B reply}", got.sender, got.data);

    line_teardown(&L);
}

// (c) connect refused when target never reserved: A.Send fails, B receives nothing,
// relay bridge count stays 0.
static void test_connect_refused_without_reservation(void) {
    line_t L;
    line_build(&L, aethernet_relay_options_default(), NULL, NULL);

    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("set_route failed"); // route known, B never reserved
    if (aethernet_relay_transport_send(L.a, "B", (const uint8_t *)"x", 1)) FAILF("A.Send should fail without a reservation");
    if (!collector_expect_none(&L.b_recv, 0, 200)) FAILF("B should not have received anything");
    if (aethernet_relay_transport_active_bridge_count(L.r) != 0)
        FAILF("relay bridge count = %d, want 0", aethernet_relay_transport_active_bridge_count(L.r));

    line_teardown(&L);
}

// (d) send fails with no route: B reserved, but A has no SetRoute.
static void test_send_fails_without_route(void) {
    line_t L;
    line_build(&L, aethernet_relay_options_default(), NULL, NULL);

    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("reserve failed");
    // no set_route
    if (aethernet_relay_transport_send(L.a, "B", (const uint8_t *)"x", 1)) FAILF("A.Send should fail with no relay route known");

    line_teardown(&L);
}

// (e) data budget 10: first 5-byte msg delivered; second 8-byte (cum 13>10) dropped
// and the bridge is torn down (relay bridge count → 0).
static void test_relay_enforces_data_budget(void) {
    aethernet_relay_options_t opts = aethernet_relay_options_default();
    opts.bridge_data_limit_bytes = 10;
    line_t L;
    line_build(&L, opts, NULL, NULL);

    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("reserve failed");
    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("set_route failed");

    const uint8_t first[5] = {1, 2, 3, 4, 5};
    if (!aethernet_relay_transport_send(L.a, "B", first, 5)) FAILF("first send failed");
    if (!collector_wait(&L.b_recv, 1, 3000, NULL)) FAILF("timeout waiting for first (in-budget) message");

    const uint8_t second[8] = {6, 7, 8, 9, 10, 11, 12, 13};
    aethernet_relay_transport_send(L.a, "B", second, 8); // 8 more -> 13 > 10 -> torn down
    if (!collector_expect_none(&L.b_recv, 1, 300)) FAILF("over-budget message should not arrive");
    if (aethernet_relay_transport_active_bridge_count(L.r) != 0)
        FAILF("bridge should be torn down on budget breach, count = %d",
              aethernet_relay_transport_active_bridge_count(L.r));

    line_teardown(&L);
}

// (f) reservation expiry via injectable clock: advance past TTL, connect refused.
static void test_reservation_expiry_refuses_connect(void) {
    clock_init(&g_clock);
    aethernet_relay_options_t opts = aethernet_relay_options_default();
    opts.reservation_ttl_ms = 30LL * 60LL * 1000LL; // 30 minutes
    line_t L;
    line_build(&L, opts, clock_now, &g_clock);

    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("reserve failed");
    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("set_route failed");

    clock_advance(&g_clock, 31LL * 60LL * 1000LL); // past the reservation TTL on R's clock

    if (aethernet_relay_transport_send(L.a, "B", (const uint8_t *)"x", 1)) FAILF("A.Send should fail after reservation expiry");
    if (!collector_expect_none(&L.b_recv, 0, 200)) FAILF("B should not receive after expiry");

    line_teardown(&L);
    pthread_mutex_destroy(&g_clock.mu);
}

// ── main ────────────────────────────────────────────────────────────────────

#define RUN(fn, label) do { \
    printf("TEST: " label "..."); fflush(stdout); \
    fn(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)

int main(void) {
    srand(1234); // deterministic conn-id stream; behaviour must not depend on values
    printf("Aether Circuit-Relay-v2 ENGINE — Behavioural Tests\n");
    printf("==================================================\n");

    RUN(test_message_traverses_relay_no_direct_link, "message_traverses_relay_no_direct_link");
    RUN(test_bridge_is_bidirectional,                "bridge_is_bidirectional");
    RUN(test_connect_refused_without_reservation,    "connect_refused_without_reservation");
    RUN(test_send_fails_without_route,               "send_fails_without_route");
    RUN(test_relay_enforces_data_budget,             "relay_enforces_data_budget");
    RUN(test_reservation_expiry_refuses_connect,     "reservation_expiry_refuses_connect");

    printf("\n%d behavioural cases passed.\n", tests_run);
    return 0;
}
