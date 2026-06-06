// SPDX-License-Identifier: MIT
// Unit tests for transport_inprocess.c — in-process transport node registration,
// data delivery, is_connected, unregister, destroy, and transport facade.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethermesh/transport.h"

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ── Callback capture ──────────────────────────────────────────

static int g_recv_count = 0;
static uint8_t g_recv_buf[256];
static size_t  g_recv_len = 0;

static void on_data(const char *from, const uint8_t *data, size_t len, void *ud) {
    (void)from; (void)ud;
    g_recv_count++;
    if (len < sizeof(g_recv_buf)) {
        memcpy(g_recv_buf, data, len);
        g_recv_len = len;
    }
}

// ── Tests ─────────────────────────────────────────────────────

static void create_returns_non_null(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    assert(t != NULL);
    aethermesh_transport_destroy(t);
}

static void register_node_returns_true(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    bool ok = aethermesh_inprocess_transport_register_node(t, "alice");
    assert(ok);
    aethermesh_transport_destroy(t);
}

static void register_same_node_twice_returns_false(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "alice");
    bool again = aethermesh_inprocess_transport_register_node(t, "alice");
    assert(!again);
    aethermesh_transport_destroy(t);
}

static void is_connected_registered_node_returns_true(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "alice");
    bool c = aethermesh_transport_is_connected(t, "alice");
    assert(c);
    aethermesh_transport_destroy(t);
}

static void is_connected_unregistered_node_returns_false(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    bool c = aethermesh_transport_is_connected(t, "nobody");
    assert(!c);
    aethermesh_transport_destroy(t);
}

static void send_delivers_data_to_registered_receiver(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "bob");

    g_recv_count = 0;
    aethermesh_transport_set_on_data_received(t, on_data, NULL);

    const uint8_t payload[] = { 0xDE, 0xAD, 0xBE, 0xEF };
    bool ok = aethermesh_transport_send(t, "bob", payload, sizeof(payload));
    assert(ok);
    assert(g_recv_count == 1);
    assert(g_recv_len == sizeof(payload));
    assert(memcmp(g_recv_buf, payload, sizeof(payload)) == 0);

    aethermesh_transport_destroy(t);
}

static void send_to_unregistered_peer_returns_false(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    const uint8_t data[] = { 0x01 };
    bool ok = aethermesh_transport_send(t, "nobody", data, sizeof(data));
    assert(!ok);
    aethermesh_transport_destroy(t);
}

static void send_without_callback_returns_false(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "carol");
    // No callback registered for carol
    const uint8_t data[] = { 0x01 };
    bool ok = aethermesh_transport_send(t, "carol", data, sizeof(data));
    assert(!ok);
    aethermesh_transport_destroy(t);
}

static void unregister_node_makes_it_unavailable(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "dave");
    assert(aethermesh_transport_is_connected(t, "dave"));

    bool unreg = aethermesh_inprocess_transport_unregister_node(t, "dave");
    assert(unreg);
    assert(!aethermesh_transport_is_connected(t, "dave"));

    aethermesh_transport_destroy(t);
}

static void unregister_unknown_node_returns_false(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    bool bad = aethermesh_inprocess_transport_unregister_node(t, "ghost");
    assert(!bad);
    aethermesh_transport_destroy(t);
}

static void multiple_nodes_can_coexist(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "node1");
    aethermesh_inprocess_transport_register_node(t, "node2");
    aethermesh_inprocess_transport_register_node(t, "node3");

    assert(aethermesh_transport_is_connected(t, "node1"));
    assert(aethermesh_transport_is_connected(t, "node2"));
    assert(aethermesh_transport_is_connected(t, "node3"));

    aethermesh_inprocess_transport_unregister_node(t, "node2");
    assert(aethermesh_transport_is_connected(t, "node1"));
    assert(!aethermesh_transport_is_connected(t, "node2"));
    assert(aethermesh_transport_is_connected(t, "node3"));

    aethermesh_transport_destroy(t);
}

static void get_metrics_returns_non_null_after_create(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    assert(t->vtable != NULL);
    assert(t->vtable->get_metrics != NULL);
    aethermesh_transport_metrics_t *m = t->vtable->get_metrics(t->handle);
    assert(m != NULL);
    // Priors must be set
    assert(m->ewma_rtt_ms == 200.0);
    assert(m->ewma_loss_rate == 0.05);
    aethermesh_transport_destroy(t);
}

static void send_updates_metrics_sample_count(void) {
    aethermesh_transport_t *t = aethermesh_inprocess_transport_new();
    aethermesh_inprocess_transport_register_node(t, "eve");
    aethermesh_transport_set_on_data_received(t, on_data, NULL);

    aethermesh_transport_metrics_t *m = t->vtable->get_metrics(t->handle);
    assert(m != NULL);
    uint64_t before = m->sample_count;

    const uint8_t data[] = { 1, 2, 3 };
    aethermesh_transport_send(t, "eve", data, sizeof(data));

    assert(m->sample_count > before);

    aethermesh_transport_destroy(t);
}

// ── main ─────────────────────────────────────────────────────

int main(void) {
    printf("Aether In-Process Transport — Unit Tests\n");
    printf("=========================================\n");

    RUN(create_returns_non_null);
    RUN(register_node_returns_true);
    RUN(register_same_node_twice_returns_false);
    RUN(is_connected_registered_node_returns_true);
    RUN(is_connected_unregistered_node_returns_false);
    RUN(send_delivers_data_to_registered_receiver);
    RUN(send_to_unregistered_peer_returns_false);
    RUN(send_without_callback_returns_false);
    RUN(unregister_node_makes_it_unavailable);
    RUN(unregister_unknown_node_returns_false);
    RUN(multiple_nodes_can_coexist);
    RUN(get_metrics_returns_non_null_after_create);
    RUN(send_updates_metrics_sample_count);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
