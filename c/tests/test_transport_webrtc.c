// SPDX-License-Identifier: MIT
// Integration tests for transport_webrtc.c — a real WebRTC RTCDataChannel
// transport over libdatachannel. Stands up two WebRtc transports wired only
// through an in-process signalling bus (no central server, no STUN), and proves
// a direct data channel negotiates over host candidates and carries bytes.
//
// Mirrors go/transport/webrtc/webrtc_test.go and the C# loopback test.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <pthread.h>

#include "aethernet/transport_webrtc.h"

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    fflush(stdout); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ── Received-byte capture (thread-safe; the data callback fires on a
//    libdatachannel internal thread) ─────────────────────────────

typedef struct {
    pthread_mutex_t lock;
    pthread_cond_t  cv;
    int             count;
    uint8_t         buf[512];
    size_t          len;
    char            from[128];
} recv_sink_t;

static void sink_init(recv_sink_t *s) {
    memset(s, 0, sizeof(*s));
    pthread_mutex_init(&s->lock, NULL);
    pthread_cond_init(&s->cv, NULL);
}

static void sink_destroy(recv_sink_t *s) {
    pthread_cond_destroy(&s->cv);
    pthread_mutex_destroy(&s->lock);
}

static void on_data(const char *from, const uint8_t *data, size_t len, void *ud) {
    recv_sink_t *s = (recv_sink_t *)ud;
    pthread_mutex_lock(&s->lock);
    s->count++;
    if (len <= sizeof(s->buf)) {
        memcpy(s->buf, data, len);
        s->len = len;
    }
    if (from) {
        strncpy(s->from, from, sizeof(s->from) - 1);
        s->from[sizeof(s->from) - 1] = '\0';
    }
    pthread_cond_broadcast(&s->cv);
    pthread_mutex_unlock(&s->lock);
}

/* Wait up to timeout_ms for at least one received message. Returns true if one
 * arrived. */
static bool sink_wait(recv_sink_t *s, long timeout_ms) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec  += timeout_ms / 1000;
    deadline.tv_nsec += (timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec  += 1;
        deadline.tv_nsec -= 1000000000L;
    }
    pthread_mutex_lock(&s->lock);
    while (s->count == 0) {
        int rc = pthread_cond_timedwait(&s->cv, &s->lock, &deadline);
        if (rc != 0) break;
    }
    bool got = s->count > 0;
    pthread_mutex_unlock(&s->lock);
    return got;
}

// ── Tests ─────────────────────────────────────────────────────

/* The headline test: two real transports, an in-process bus, host-only ICE,
 * and a byte payload that must arrive at the responder. */
static void two_peers_exchange_bytes_no_server(void) {
    aethernet_webrtc_signaling_bus_t *bus = aethernet_webrtc_signaling_bus_new();
    assert(bus != NULL);

    aethernet_webrtc_signaling_t *alice_sig =
        aethernet_webrtc_signaling_bus_endpoint(bus, "alice");
    aethernet_webrtc_signaling_t *bob_sig =
        aethernet_webrtc_signaling_bus_endpoint(bus, "bob");
    assert(alice_sig != NULL && bob_sig != NULL);

    /* count == 0 => host-candidate-only ICE, no network dependency. */
    aethernet_transport_t *alice =
        aethernet_webrtc_transport_new("alice", alice_sig, NULL, 0);
    aethernet_transport_t *bob =
        aethernet_webrtc_transport_new("bob", bob_sig, NULL, 0);
    assert(alice != NULL && bob != NULL);

    recv_sink_t sink;
    sink_init(&sink);
    aethernet_transport_set_on_data_received(bob, on_data, &sink);

    const uint8_t payload[] = "hello over a serverless webrtc datachannel";
    size_t payload_len = sizeof(payload) - 1; /* drop the trailing NUL */

    bool ok = aethernet_transport_send(alice, "bob", payload, payload_len);
    assert(ok);

    /* Allow generous time for ICE + DTLS + SCTP to come up on a loaded host. */
    bool got = sink_wait(&sink, 30000);
    assert(got);
    assert(sink.len == payload_len);
    assert(memcmp(sink.buf, payload, payload_len) == 0);
    assert(strcmp(sink.from, "alice") == 0);

    assert(aethernet_transport_is_connected(alice, "bob"));
    assert(aethernet_transport_is_connected(bob, "alice"));

    sink_destroy(&sink);
    aethernet_transport_destroy(alice);
    aethernet_transport_destroy(bob);
    aethernet_webrtc_signaling_bus_destroy(bus);
}

/* Ladder-facing metadata, mirroring the Go TestTransportMetadata. */
static void transport_metadata_is_correct(void) {
    aethernet_webrtc_signaling_bus_t *bus = aethernet_webrtc_signaling_bus_new();
    assert(bus != NULL);
    aethernet_webrtc_signaling_t *sig =
        aethernet_webrtc_signaling_bus_endpoint(bus, "x");
    aethernet_transport_t *t = aethernet_webrtc_transport_new("x", sig, NULL, 0);
    assert(t != NULL);
    assert(t->vtable != NULL);

    assert(strcmp(t->vtable->name, "WebRTC P2P") == 0);
    assert(t->vtable->max_range_meters == 0);          /* internet — unbounded */
    assert(t->vtable->max_bandwidth_bps == 100000000);
    assert(t->vtable->get_metrics != NULL);
    aethernet_transport_metrics_t *m = t->vtable->get_metrics(t->handle);
    assert(m != NULL);
    assert(m->ewma_rtt_ms == 200.0);   /* priors set by metrics_init */

    aethernet_transport_destroy(t);
    aethernet_webrtc_signaling_bus_destroy(bus);
}

/* A reverse send proves the channel is bidirectional once negotiated. */
static void bytes_flow_both_directions(void) {
    aethernet_webrtc_signaling_bus_t *bus = aethernet_webrtc_signaling_bus_new();
    assert(bus != NULL);

    aethernet_transport_t *alice =
        aethernet_webrtc_transport_new("alice",
            aethernet_webrtc_signaling_bus_endpoint(bus, "alice"), NULL, 0);
    aethernet_transport_t *bob =
        aethernet_webrtc_transport_new("bob",
            aethernet_webrtc_signaling_bus_endpoint(bus, "bob"), NULL, 0);
    assert(alice != NULL && bob != NULL);

    recv_sink_t at_bob, at_alice;
    sink_init(&at_bob);
    sink_init(&at_alice);
    aethernet_transport_set_on_data_received(bob, on_data, &at_bob);
    aethernet_transport_set_on_data_received(alice, on_data, &at_alice);

    const uint8_t a2b[] = { 0xDE, 0xAD, 0xBE, 0xEF };
    assert(aethernet_transport_send(alice, "bob", a2b, sizeof(a2b)));
    assert(sink_wait(&at_bob, 30000));
    assert(at_bob.len == sizeof(a2b));
    assert(memcmp(at_bob.buf, a2b, sizeof(a2b)) == 0);

    /* Now bob -> alice over the (already-open) reverse path. */
    const uint8_t b2a[] = { 0x01, 0x02, 0x03 };
    assert(aethernet_transport_send(bob, "alice", b2a, sizeof(b2a)));
    assert(sink_wait(&at_alice, 30000));
    assert(at_alice.len == sizeof(b2a));
    assert(memcmp(at_alice.buf, b2a, sizeof(b2a)) == 0);

    sink_destroy(&at_bob);
    sink_destroy(&at_alice);
    aethernet_transport_destroy(alice);
    aethernet_transport_destroy(bob);
    aethernet_webrtc_signaling_bus_destroy(bus);
}

// ── main ─────────────────────────────────────────────────────

int main(void) {
    printf("Aether WebRTC P2P Transport — Integration Tests\n");
    printf("================================================\n");

    RUN(transport_metadata_is_correct);
    RUN(two_peers_exchange_bytes_no_server);
    RUN(bytes_flow_both_directions);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
