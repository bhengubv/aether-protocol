// SPDX-License-Identifier: MIT
// Mesh-integration proof for the native circuit-relay-v2 ENGINE + mesh RelayLink
// (c/src/relay_mesh_link.c). A three-node topology A ── R ── B with NO direct A-B edge:
// a message from A must traverse the relay bridge to reach B, and every hop travels as a
// real aethernet_mesh_packet_t of type AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL —
// exactly how a host mesh consumes it. Mirrors go/circuitrelay/meshlink_test.go's
// TestRelayWorksAsMeshTransport and the C# CircuitRelayMeshIntegrationTests.
//
// The in-process mesh hub is the seam that stands in for the real radios: its send_one_hop
// receives a borrowed CircuitRelayControl packet, CLONES it, and delivers it to the
// destination node's mesh link on a DETACHED pthread (async one-hop, like Go's `go`),
// which feeds it into that node's bound relay transport. Received endpoint data lands in a
// thread-safe collector waited on with a timeout.

#define _POSIX_C_SOURCE 200809L

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/protocol.h"
#include "aethernet/relay_mesh_link.h"
#include "aethernet/relay_transport.h"

// ── test runner ─────────────────────────────────────────────────────────────

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

// ── in-process one-hop mesh over MeshRelayLinks ─────────────────────────────
//
// Nodes register a mesh link by name; connect(x,y) makes an undirected edge. A node's
// send_one_hop delivers a CircuitRelayControl packet to the target's mesh link on a
// detached thread, but only across an existing edge — the same mesh semantics as the
// engine test, one layer up (whole MeshPackets, not raw frames).

#define MESH_MAX_NODES 8
#define MESH_MAX_EDGES 32

typedef struct {
    char name[64];
    aethernet_relay_mesh_link_t *link;
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

// A per-node callback context: which mesh, and this node's own name (the `from`).
typedef struct {
    mesh_t *mesh;
    char node[64];
} node_ctx_t;

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

static void mesh_register(mesh_t *m, const char *name, aethernet_relay_mesh_link_t *link) {
    pthread_mutex_lock(&m->mu);
    mesh_node_t *n = &m->nodes[m->node_count++];
    snprintf(n->name, sizeof n->name, "%s", name);
    n->link = link;
    pthread_mutex_unlock(&m->mu);
}

static aethernet_relay_mesh_link_t *mesh_link_for(mesh_t *m, const char *name) {
    pthread_mutex_lock(&m->mu);
    aethernet_relay_mesh_link_t *l = NULL;
    for (int i = 0; i < m->node_count; i++)
        if (strcmp(m->nodes[i].name, name) == 0) { l = m->nodes[i].link; break; }
    pthread_mutex_unlock(&m->mu);
    return l;
}

// A single async one-hop delivery: owns a CLONE of the borrowed packet, delivers it to
// the destination mesh link's handle_incoming_packet, then frees the clone. Detached.
typedef struct {
    mesh_t *mesh;
    char to[64];
    aethernet_mesh_packet_t *pkt; // owned clone
} hop_t;

static void *hop_run(void *arg) {
    hop_t *h = (hop_t *)arg;
    aethernet_relay_mesh_link_t *l = mesh_link_for(h->mesh, h->to);
    if (l) aethernet_relay_mesh_link_handle_incoming_packet(l, h->pkt);
    aethernet_packet_free(h->pkt);
    free(h);
    return NULL;
}

// ── host callbacks: the mesh seam (send_one_hop / can_reach) ────────────────

static bool node_send_one_hop(void *ctx, const aethernet_mesh_packet_t *packet) {
    node_ctx_t *nc = (node_ctx_t *)ctx;
    if (!packet || !packet->destination_uhid) return false;
    if (!mesh_adjacent(nc->mesh, nc->node, packet->destination_uhid)) return false;

    // The packet is borrowed (freed by the mesh link right after this returns) — clone it
    // for the async hop, exactly as a real transport that queues bytes would.
    aethernet_mesh_packet_t *clone = aethernet_packet_clone(packet);
    if (!clone) return false;

    hop_t *h = (hop_t *)calloc(1, sizeof(hop_t));
    if (!h) { aethernet_packet_free(clone); return false; }
    h->mesh = nc->mesh;
    snprintf(h->to, sizeof h->to, "%s", packet->destination_uhid);
    h->pkt = clone;

    pthread_t th;
    if (pthread_create(&th, NULL, hop_run, h) != 0) {
        aethernet_packet_free(clone);
        free(h);
        return false;
    }
    pthread_detach(th);
    return true;
}

static bool node_can_reach(void *ctx, const char *node) {
    node_ctx_t *nc = (node_ctx_t *)ctx;
    return mesh_adjacent(nc->mesh, nc->node, node);
}

// ── thread-safe receive collector ───────────────────────────────────────────

typedef struct {
    char sender[128];
    uint8_t data[512];
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
        uint32_t n = len < sizeof(r->data) ? len : (uint32_t)sizeof(r->data);
        if (n && data) memcpy(r->data, data, n);
        r->len = len;
    }
    pthread_cond_broadcast(&c->cond);
    pthread_mutex_unlock(&c->mu);
}

// Wait until at least `want` items have arrived or `timeout_ms` elapses. Returns true if
// the count was reached, copying the item at index `want-1` into *out.
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

// ── line topology: A ── R ── B with NO A-B edge ─────────────────────────────

typedef struct {
    mesh_t mesh;
    node_ctx_t ctx_a, ctx_r, ctx_b;
    aethernet_relay_mesh_link_t *link_a, *link_r, *link_b;
    aethernet_relay_link_t vt_a, vt_r, vt_b;
    aethernet_relay_transport_t *a, *r, *b;
    collector_t b_recv;
} line_t;

static void make_node(mesh_t *m, node_ctx_t *ctx, const char *name,
                      aethernet_relay_mesh_link_t **out_link) {
    ctx->mesh = m;
    snprintf(ctx->node, sizeof ctx->node, "%s", name);
    aethernet_relay_mesh_link_t *link =
        aethernet_relay_mesh_link_new(name, node_send_one_hop, ctx, node_can_reach, ctx);
    if (!link) FAILF("relay_mesh_link_new(%s) returned NULL", name);
    *out_link = link;
}

static void line_build(line_t *L) {
    mesh_init(&L->mesh);
    mesh_connect(&L->mesh, "A", "R");
    mesh_connect(&L->mesh, "R", "B"); // deliberately NO A-B edge

    make_node(&L->mesh, &L->ctx_a, "A", &L->link_a);
    make_node(&L->mesh, &L->ctx_r, "R", &L->link_r);
    make_node(&L->mesh, &L->ctx_b, "B", &L->link_b);

    // Register mesh links so send_one_hop can locate the destination node's link.
    mesh_register(&L->mesh, "A", L->link_a);
    mesh_register(&L->mesh, "R", L->link_r);
    mesh_register(&L->mesh, "B", L->link_b);

    // Build the engine vtable from each mesh link and stand up the transports.
    L->vt_a = aethernet_relay_mesh_link_as_link(L->link_a);
    L->vt_r = aethernet_relay_mesh_link_as_link(L->link_r);
    L->vt_b = aethernet_relay_mesh_link_as_link(L->link_b);

    L->a = aethernet_relay_transport_new("A", &L->vt_a, aethernet_relay_options_default(), NULL, NULL);
    L->r = aethernet_relay_transport_new("R", &L->vt_r, aethernet_relay_options_default(), NULL, NULL);
    L->b = aethernet_relay_transport_new("B", &L->vt_b, aethernet_relay_options_default(), NULL, NULL);
    if (!L->a || !L->r || !L->b) FAILF("relay_transport_new returned NULL");

    // Bind each transport to its mesh link so inbound CircuitRelayControl packets feed the engine.
    aethernet_relay_mesh_link_bind_transport(L->link_a, L->a);
    aethernet_relay_mesh_link_bind_transport(L->link_r, L->r);
    aethernet_relay_mesh_link_bind_transport(L->link_b, L->b);

    collector_init(&L->b_recv);
    aethernet_relay_transport_set_on_data(L->b, collector_on_data, &L->b_recv);
}

static void line_teardown(line_t *L) {
    // Transports first (they hold the vtable ctx = mesh link), then the mesh links.
    aethernet_relay_transport_destroy(L->a);
    aethernet_relay_transport_destroy(L->r);
    aethernet_relay_transport_destroy(L->b);
    aethernet_relay_mesh_link_destroy(L->link_a);
    aethernet_relay_mesh_link_destroy(L->link_r);
    aethernet_relay_mesh_link_destroy(L->link_b);
    collector_destroy(&L->b_recv);
    mesh_destroy(&L->mesh);
}

// A→R→B relayed over real CircuitRelayControl MeshPackets, no direct A-B link:
// B receives {A, "deadbeef"}; the relay's active bridge count == 1.
static void test_relay_works_as_mesh_transport(void) {
    line_t L;
    line_build(&L);

    if (aethernet_relay_transport_is_connected(L.a, "B")) FAILF("A should have no direct path to B");
    if (!aethernet_relay_transport_reserve(L.b, "R")) FAILF("B failed to reserve on R");
    if (!aethernet_relay_transport_set_route(L.a, "B", "R")) FAILF("A.set_route failed");

    const uint8_t payload[4] = {0xDE, 0xAD, 0xBE, 0xEF};
    if (!aethernet_relay_transport_send(L.a, "B", payload, 4)) FAILF("A.Send to B failed");

    recv_t got;
    if (!collector_wait(&L.b_recv, 1, 3000, &got))
        FAILF("B never received the relayed message via the mesh link");
    if (strcmp(got.sender, "A") != 0)
        FAILF("sender = %s, want A", got.sender);
    if (got.len != 4 || memcmp(got.data, payload, 4) != 0)
        FAILF("payload mismatch (len=%u), want DE AD BE EF", got.len);
    if (aethernet_relay_transport_active_bridge_count(L.r) != 1)
        FAILF("relay bridge count = %d, want 1", aethernet_relay_transport_active_bridge_count(L.r));

    line_teardown(&L);
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
    printf("Aether Circuit-Relay-v2 MESH-INTEGRATION — Behavioural Test\n");
    printf("===========================================================\n");

    RUN(test_relay_works_as_mesh_transport, "relay_works_as_mesh_transport");

    printf("\n%d behavioural case%s passed.\n", tests_run, tests_run == 1 ? "" : "s");
    return 0;
}
