// SPDX-License-Identifier: MIT
// Gap-2 acceptance test: the circuit-relay-v2 engine, wrapped as a generic TRANSPORT
// (aethernet/mesh_circuit_relay.h), must be AUTO-SELECTED by the minimal transport manager
// (aethernet/transport_manager.h) as the last-resort serverless fallback — NOT called directly.
//
// Topology: 3-node in-process hub A ── R ── B with NO direct A-B edge. Each node's relay is built
// through the factory (aethernet_mesh_circuit_relay_create → a mesh RelayLink + engine + transport)
// and wrapped in a manager as that node's ONLY transport. B reserves on R; A routes B via R; A
// sends the payload through the MANAGER (never the transport directly). The manager, having only
// the cost-90 relay, selects it; B receives the exact payload, tagged with the relay transport's
// name "Circuit Relay (v2)" — proving selection, not hand-wiring; and R shows exactly one active
// bridge, proving a real relayed hop over CircuitRelayControl (type-57) MeshPackets.
//
// Mirrors the C# Relay_Is_Auto_Selected_By_TransportManager_As_Fallback
// (tests/AetherNet.Core.Tests/CircuitRelayMeshIntegrationTests.cs) and the Go
// TestRelay_Is_Auto_Selected_By_Manager_As_Fallback (go/circuitrelay/manager_integration_test.go).
//
// The in-process mesh hub is the seam standing in for the real radios: send_one_hop CLONES the
// borrowed CircuitRelayControl packet and delivers it to the destination node's mesh link on a
// DETACHED pthread (async one-hop), which feeds it into that node's bound relay engine. This is the
// same hub used by test_relay_mesh.c, one layer up (driven via the factory + manager).

#define _POSIX_C_SOURCE 200809L

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/mesh_circuit_relay.h"
#include "aethernet/protocol.h"
#include "aethernet/relay_mesh_link.h"
#include "aethernet/relay_transport.h"
#include "aethernet/transport_manager.h"

// ── test runner ─────────────────────────────────────────────────────────────

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

// ── in-process one-hop mesh over MeshRelayLinks (same shape as test_relay_mesh.c) ──

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

// A single async one-hop delivery: owns a CLONE of the borrowed packet, delivers it to the
// destination mesh link's handle_incoming_packet, then frees the clone. Detached.
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

// Host callbacks: the mesh seam (send_one_hop / can_reach).
static bool node_send_one_hop(void *ctx, const aethernet_mesh_packet_t *packet) {
    node_ctx_t *nc = (node_ctx_t *)ctx;
    if (!packet || !packet->destination_uhid) return false;
    if (!mesh_adjacent(nc->mesh, nc->node, packet->destination_uhid)) return false;

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

// ── thread-safe receive collector (records the `via` tag from the manager) ──

typedef struct {
    char sender[128];
    char via[128];
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

// Manager-level on-data: (sender, data, len, via, user_data).
static void collector_on_data(const char *sender, const uint8_t *data, size_t len,
                              const char *via, void *ud) {
    collector_t *c = (collector_t *)ud;
    pthread_mutex_lock(&c->mu);
    if (c->count < (int)(sizeof c->items / sizeof c->items[0])) {
        recv_t *r = &c->items[c->count++];
        snprintf(r->sender, sizeof r->sender, "%s", sender ? sender : "");
        snprintf(r->via, sizeof r->via, "%s", via ? via : "");
        uint32_t n = len < sizeof(r->data) ? (uint32_t)len : (uint32_t)sizeof(r->data);
        if (n && data) memcpy(r->data, data, n);
        r->len = (uint32_t)len;
    }
    pthread_cond_broadcast(&c->cond);
    pthread_mutex_unlock(&c->mu);
}

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

// ── the acceptance case ─────────────────────────────────────────────────────

typedef struct {
    mesh_t mesh;
    node_ctx_t ctx_a, ctx_r, ctx_b;
    aethernet_relay_mesh_link_t *link_a, *link_r, *link_b;
    aethernet_transport_t *tA, *tR, *tB;   // relay transports (own their engine + link)
    aethernet_transport_manager_t *mgr_a, *mgr_b;
    collector_t b_recv;
} scenario_t;

static void make_relay_transport(mesh_t *m, node_ctx_t *ctx, const char *name,
                                 aethernet_relay_mesh_link_t **out_link,
                                 aethernet_transport_t **out_transport) {
    ctx->mesh = m;
    snprintf(ctx->node, sizeof ctx->node, "%s", name);

    aethernet_relay_mesh_link_t *link = NULL;
    aethernet_transport_t *t = aethernet_mesh_circuit_relay_create(
        name, node_send_one_hop, ctx, node_can_reach, ctx,
        aethernet_relay_options_default(), &link);
    if (!t || !link) FAILF("mesh_circuit_relay_create(%s) returned NULL", name);

    *out_link = link;
    *out_transport = t;
}

static void scenario_build(scenario_t *S) {
    mesh_init(&S->mesh);
    mesh_connect(&S->mesh, "A", "R");
    mesh_connect(&S->mesh, "R", "B"); // deliberately NO A-B edge

    make_relay_transport(&S->mesh, &S->ctx_a, "A", &S->link_a, &S->tA);
    make_relay_transport(&S->mesh, &S->ctx_r, "R", &S->link_r, &S->tR);
    make_relay_transport(&S->mesh, &S->ctx_b, "B", &S->link_b, &S->tB);

    // Register the factory-produced mesh links so send_one_hop can locate the destination's link.
    mesh_register(&S->mesh, "A", S->link_a);
    mesh_register(&S->mesh, "R", S->link_r);
    mesh_register(&S->mesh, "B", S->link_b);

    // A and B each run a manager whose ONLY transport is the relay (no BLE/Wi-Fi/NearLink), so if
    // the message arrives it can only be because the manager selected the relay.
    aethernet_transport_t *a_only[1] = { S->tA };
    aethernet_transport_t *b_only[1] = { S->tB };
    S->mgr_a = aethernet_transport_manager_new(a_only, 1);
    S->mgr_b = aethernet_transport_manager_new(b_only, 1);
    if (!S->mgr_a || !S->mgr_b) FAILF("transport_manager_new returned NULL");

    collector_init(&S->b_recv);
    aethernet_transport_manager_set_on_data(S->mgr_b, collector_on_data, &S->b_recv);
}

static void scenario_teardown(scenario_t *S) {
    // Managers first (they only detach their trampoline; own nothing), then the transports (which
    // own + free their engine and mesh link).
    aethernet_transport_manager_destroy(S->mgr_a);
    aethernet_transport_manager_destroy(S->mgr_b);
    aethernet_transport_destroy(S->tA);
    aethernet_transport_destroy(S->tR);
    aethernet_transport_destroy(S->tB);
    collector_destroy(&S->b_recv);
    mesh_destroy(&S->mesh);
}

static void test_relay_is_auto_selected_by_manager_as_fallback(void) {
    scenario_t S;
    scenario_build(&S);

    // Sanity: no direct A-B path exists yet.
    if (aethernet_relay_transport_is_connected(
            aethernet_mesh_circuit_relay_engine(S.tA), "B"))
        FAILF("A should have no direct path to B");

    // B advertises reachability by reserving on R; A learns B is reachable via R.
    if (!aethernet_mesh_circuit_relay_reserve(S.tB, "R")) FAILF("B failed to reserve on R");
    if (!aethernet_mesh_circuit_relay_set_route(S.tA, "B", "R")) FAILF("A.set_route failed");

    const uint8_t payload[4] = {0x11, 0x22, 0x33, 0x44};

    // Send via the MANAGER — which must select the relay (its only, last-resort transport).
    if (!aethernet_transport_manager_send(S.mgr_a, "B", payload, 4))
        FAILF("A manager send returned false — the relay was not selected");

    recv_t got;
    if (!collector_wait(&S.b_recv, 1, 3000, &got))
        FAILF("B never received the relayed message via transport-manager selection");
    if (strcmp(got.sender, "A") != 0)
        FAILF("sender = %s, want A", got.sender);
    if (got.len != 4 || memcmp(got.data, payload, 4) != 0)
        FAILF("payload mismatch (len=%u), want 11 22 33 44", got.len);
    if (strcmp(got.via, AETHERNET_CIRCUIT_RELAY_TRANSPORT_NAME) != 0)
        FAILF("via = %s, want %s (manager must tag the selected transport)",
              got.via, AETHERNET_CIRCUIT_RELAY_TRANSPORT_NAME);
    if (aethernet_mesh_circuit_relay_active_bridge_count(S.tR) != 1)
        FAILF("relay bridge count on R = %d, want 1 (R must be genuinely bridging)",
              aethernet_mesh_circuit_relay_active_bridge_count(S.tR));

    scenario_teardown(&S);
}

// A second, focused check: the manager orders transports ascending by power cost, so the cost-90
// relay sorts AFTER a cheaper stub — i.e. it is genuinely the last-resort fallback, not first.
static bool stub_send(void *h, const char *peer, const uint8_t *d, size_t n) {
    (void)h; (void)peer; (void)d; (void)n; return false; // always declines
}
static bool stub_is_connected(void *h, const char *peer) { (void)h; (void)peer; return false; }
static void stub_set_on_data(void *h, aethernet_transport_on_data_received cb, void *ud) {
    (void)h; (void)cb; (void)ud; // no-op: these stubs never deliver
}

// Build a throwaway transport with a given power cost and name (no engine; send always declines).
// The vtable is a caller-owned stack variable (same pattern as test_transport_metrics.c); destroy
// is NULL and the manager never destroys it, so the caller just free()s the returned wrapper.
static aethernet_transport_t *make_stub_transport(int power, const char *name,
                                                  aethernet_transport_vtable_t *vt) {
    memset(vt, 0, sizeof(*vt));
    vt->name = name;
    vt->send = stub_send;
    vt->is_connected = stub_is_connected;
    vt->set_on_data_received = stub_set_on_data;
    vt->destroy = NULL;
    vt->power_cost_relative = power;
    aethernet_transport_t *t = (aethernet_transport_t *)calloc(1, sizeof(*t));
    if (!t) FAILF("stub transport alloc failed");
    t->vtable = vt;
    t->handle = NULL; // no state; the stub methods ignore the handle
    return t;
}

static void test_manager_orders_relay_last_by_power_cost(void) {
    // A cheap stub (cost 1) + a relay-cost stub (cost 90). The manager must order cheap-first.
    aethernet_transport_vtable_t vt_cheap, vt_relay;
    aethernet_transport_t *cheap = make_stub_transport(1, "Cheap", &vt_cheap);
    aethernet_transport_t *relay = make_stub_transport(
        AETHERNET_CIRCUIT_RELAY_POWER_COST, AETHERNET_CIRCUIT_RELAY_TRANSPORT_NAME, &vt_relay);

    // Register in the "wrong" order (relay first) to prove the sort reorders by cost, not input.
    aethernet_transport_t *in[2] = { relay, cheap };
    aethernet_transport_manager_t *mgr = aethernet_transport_manager_new(in, 2);
    if (!mgr) FAILF("transport_manager_new returned NULL");

    if (aethernet_transport_manager_count(mgr) != 2)
        FAILF("manager count = %zu, want 2", aethernet_transport_manager_count(mgr));

    aethernet_transport_t *first = aethernet_transport_manager_at(mgr, 0);
    aethernet_transport_t *last = aethernet_transport_manager_at(mgr, 1);
    if (!first || !last || first->vtable->power_cost_relative != 1)
        FAILF("lowest-cost transport not ordered first");
    if (last->vtable->power_cost_relative != AETHERNET_CIRCUIT_RELAY_POWER_COST)
        FAILF("cost-90 relay not ordered last (got cost %d)", last->vtable->power_cost_relative);

    // Both decline → manager reports overall failure (no transport reachable).
    const uint8_t p[1] = {0x00};
    if (aethernet_transport_manager_send(mgr, "X", p, 1))
        FAILF("manager send should fail when every transport declines");

    aethernet_transport_manager_destroy(mgr);
    free(cheap);
    free(relay);
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
    printf("Aether Circuit-Relay-v2 TRANSPORT-MANAGER auto-selection — Behavioural Test\n");
    printf("==========================================================================\n");

    RUN(test_relay_is_auto_selected_by_manager_as_fallback,
        "relay_is_auto_selected_by_transport_manager_as_fallback");
    RUN(test_manager_orders_relay_last_by_power_cost,
        "manager_orders_relay_last_by_power_cost");

    printf("\n%d behavioural case%s passed.\n", tests_run, tests_run == 1 ? "" : "s");
    return 0;
}
