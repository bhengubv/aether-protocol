// SPDX-License-Identifier: MIT
// Unit tests for transport_inprocess.c — in-process transport node registration,
// data delivery, is_connected, unregister, destroy, and transport facade.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/transport.h"

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
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    assert(t != NULL);
    aethernet_transport_destroy(t);
}

static void register_node_returns_true(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    bool ok = aethernet_inprocess_transport_register_node(t, "alice");
    assert(ok);
    aethernet_transport_destroy(t);
}

static void register_same_node_twice_returns_false(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "alice");
    bool again = aethernet_inprocess_transport_register_node(t, "alice");
    assert(!again);
    aethernet_transport_destroy(t);
}

static void is_connected_registered_node_returns_true(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "alice");
    bool c = aethernet_transport_is_connected(t, "alice");
    assert(c);
    aethernet_transport_destroy(t);
}

static void is_connected_unregistered_node_returns_false(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    bool c = aethernet_transport_is_connected(t, "nobody");
    assert(!c);
    aethernet_transport_destroy(t);
}

static void send_delivers_data_to_registered_receiver(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "bob");

    g_recv_count = 0;
    aethernet_transport_set_on_data_received(t, on_data, NULL);

    const uint8_t payload[] = { 0xDE, 0xAD, 0xBE, 0xEF };
    bool ok = aethernet_transport_send(t, "bob", payload, sizeof(payload));
    assert(ok);
    assert(g_recv_count == 1);
    assert(g_recv_len == sizeof(payload));
    assert(memcmp(g_recv_buf, payload, sizeof(payload)) == 0);

    aethernet_transport_destroy(t);
}

static void send_to_unregistered_peer_returns_false(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    const uint8_t data[] = { 0x01 };
    bool ok = aethernet_transport_send(t, "nobody", data, sizeof(data));
    assert(!ok);
    aethernet_transport_destroy(t);
}

static void send_without_callback_returns_false(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "carol");
    // No callback registered for carol
    const uint8_t data[] = { 0x01 };
    bool ok = aethernet_transport_send(t, "carol", data, sizeof(data));
    assert(!ok);
    aethernet_transport_destroy(t);
}

static void unregister_node_makes_it_unavailable(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "dave");
    assert(aethernet_transport_is_connected(t, "dave"));

    bool unreg = aethernet_inprocess_transport_unregister_node(t, "dave");
    assert(unreg);
    assert(!aethernet_transport_is_connected(t, "dave"));

    aethernet_transport_destroy(t);
}

static void unregister_unknown_node_returns_false(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    bool bad = aethernet_inprocess_transport_unregister_node(t, "ghost");
    assert(!bad);
    aethernet_transport_destroy(t);
}

static void multiple_nodes_can_coexist(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "node1");
    aethernet_inprocess_transport_register_node(t, "node2");
    aethernet_inprocess_transport_register_node(t, "node3");

    assert(aethernet_transport_is_connected(t, "node1"));
    assert(aethernet_transport_is_connected(t, "node2"));
    assert(aethernet_transport_is_connected(t, "node3"));

    aethernet_inprocess_transport_unregister_node(t, "node2");
    assert(aethernet_transport_is_connected(t, "node1"));
    assert(!aethernet_transport_is_connected(t, "node2"));
    assert(aethernet_transport_is_connected(t, "node3"));

    aethernet_transport_destroy(t);
}

static void get_metrics_returns_non_null_after_create(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    assert(t->vtable != NULL);
    assert(t->vtable->get_metrics != NULL);
    aethernet_transport_metrics_t *m = t->vtable->get_metrics(t->handle);
    assert(m != NULL);
    // Priors must be set
    assert(m->ewma_rtt_ms == 200.0);
    assert(m->ewma_loss_rate == 0.05);
    aethernet_transport_destroy(t);
}

static void send_updates_metrics_sample_count(void) {
    aethernet_transport_t *t = aethernet_inprocess_transport_new();
    aethernet_inprocess_transport_register_node(t, "eve");
    aethernet_transport_set_on_data_received(t, on_data, NULL);

    aethernet_transport_metrics_t *m = t->vtable->get_metrics(t->handle);
    assert(m != NULL);
    uint64_t before = m->sample_count;

    const uint8_t data[] = { 1, 2, 3 };
    aethernet_transport_send(t, "eve", data, sizeof(data));

    assert(m->sample_count > before);

    aethernet_transport_destroy(t);
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
