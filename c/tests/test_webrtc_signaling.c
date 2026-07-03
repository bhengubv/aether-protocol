// SPDX-License-Identifier: MIT
// Acceptance test for webrtc_signaling.c — the transport-backed WebRTC
// signalling carrier (AWS1 + JSON framing), the C mirror of C#
// RelaySignalingTests.
//
// LEVEL ACHIEVED (unconditional): two SEPARATE carrier instances (two nodes)
// over an in-process LOOPBACK TRANSPORT PAIR round-trip a full OFFER and ANSWER
// signal across the transport boundary — proving the carrier frames, sends,
// receives and parses signals over a real transport, byte-identically to C#.
// This mirrors C# RelaySignalingTests wiring two LoopbackTransport endpoints to
// each other and running RelayWebRtcSignaling over them.
//
// LEVEL ACHIEVED (only with -DAETHERNET_WITH_WEBRTC=ON, i.e. libdatachannel
// present): two real aethernet_webrtc_transport instances, each driven by a
// carrier over the same loopback pair, negotiate a direct data channel over the
// carried handshake and carry a byte payload peer-to-peer — proving the carrier
// plugs into the production WebRTC signalling seam.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <pthread.h>

#include "aethernet/transport.h"
#include "aethernet/webrtc_signaling.h"

#ifdef AETHERNET_WITH_WEBRTC
#include <time.h>
#endif

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    fflush(stdout); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

/* ───────────────────────── loopback transport pair ────────────────────────
 *
 * A minimal aethernet_transport_t whose send() delivers the bytes to its PEER
 * handle's registered data callback — the C mirror of the C# LoopbackTransport
 * (aliceRelay.Peer = bobRelay). Two of these wired to each other form the
 * transport pair the carrier rides. Delivery is synchronous, which is fine for
 * the round-trip assertions; the WebRTC transport tolerates re-entrant delivery
 * because it starts each handshake outside its own lock.
 */

typedef struct loopback_state {
    struct loopback_state              *peer;   /* where our send() delivers */
    aethernet_transport_on_data_received cb;    /* our own inbound callback  */
    void                                *cb_user;
    char                                 label[64];
} loopback_state_t;

static bool loopback_send(void *handle, const char *peer_uhid,
                          const uint8_t *data, size_t data_len) {
    (void)peer_uhid;  /* a loopback link has exactly one peer */
    loopback_state_t *s = (loopback_state_t *)handle;
    if (!s || !s->peer) return false;
    loopback_state_t *dst = s->peer;
    if (!dst->cb) return false;
    /* The in-process transports pass an empty sender UHID; the carrier reads the
     * real sender from the JSON body, so mirror that here. */
    dst->cb("", data, data_len, dst->cb_user);
    return true;
}

static bool loopback_is_connected(void *handle, const char *peer_uhid) {
    (void)peer_uhid;
    loopback_state_t *s = (loopback_state_t *)handle;
    return s && s->peer && s->peer->cb != NULL;
}

static void loopback_set_on_data_received(void *handle,
                                          aethernet_transport_on_data_received cb,
                                          void *user_data) {
    loopback_state_t *s = (loopback_state_t *)handle;
    if (!s) return;
    s->cb      = cb;
    s->cb_user = user_data;
}

static void loopback_destroy(void *handle) {
    free(handle);  /* frees the loopback_state_t */
}

/* Build one loopback transport endpoint (vtable + state). */
static aethernet_transport_t *loopback_new(const char *label) {
    aethernet_transport_t *t = (aethernet_transport_t *)malloc(sizeof(*t));
    loopback_state_t *s = (loopback_state_t *)calloc(1, sizeof(*s));
    aethernet_transport_vtable_t *v =
        (aethernet_transport_vtable_t *)calloc(1, sizeof(*v));
    assert(t && s && v);
    strncpy(s->label, label ? label : "", sizeof(s->label) - 1);
    v->name                = "loopback";
    v->send                = loopback_send;
    v->is_connected        = loopback_is_connected;
    v->set_on_data_received = loopback_set_on_data_received;
    v->destroy             = loopback_destroy;
    t->vtable = v;
    t->handle = s;
    return t;
}

/* Wire two loopback endpoints to each other. */
static void loopback_pair_wire(aethernet_transport_t *a, aethernet_transport_t *b) {
    ((loopback_state_t *)a->handle)->peer = (loopback_state_t *)b->handle;
    ((loopback_state_t *)b->handle)->peer = (loopback_state_t *)a->handle;
}

/* ───────────────────────── signal capture sink ────────────────────────────*/

typedef struct {
    int                        count;
    aethernet_webrtc_signal_t  last;
} signal_sink_t;

static void on_signal(const aethernet_webrtc_signal_t *sig, void *user) {
    signal_sink_t *s = (signal_sink_t *)user;
    s->count++;
    memcpy(&s->last, sig, sizeof(*sig));
}

/* ───────────────────────── tests: framing byte-identity ───────────────────*/

/* The AWS1 magic prefix is exactly the four C# bytes, in order. */
static void frame_magic_is_AWS1(void) {
    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof(sig));
    strcpy(sig.from_uhid, "a");
    strcpy(sig.to_uhid, "b");
    sig.type = AETHERNET_WEBRTC_SIGNAL_OFFER;
    strcpy(sig.sdp, "v=0");

    size_t len = 0;
    uint8_t *frame = aethernet_webrtc_signal_frame_encode(&sig, &len);
    assert(frame && len > 4);
    assert(frame[0] == 'A' && frame[1] == 'W' && frame[2] == 'S' && frame[3] == '1');
    assert(aethernet_webrtc_signal_frame_has_magic(frame, len));
    free(frame);
}

/* An OFFER's JSON body carries PascalCase keys, a numeric Type, the Sdp, and
 * SdpMLineIndex — and OMITS the null Candidate / SdpMid — matching C#. */
static void offer_json_body_matches_csharp_shape(void) {
    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof(sig));
    strcpy(sig.from_uhid, "alice");
    strcpy(sig.to_uhid, "bob");
    sig.type = AETHERNET_WEBRTC_SIGNAL_OFFER;
    strcpy(sig.sdp, "v=0\r\no=- 1 1 IN IP4 0.0.0.0");

    size_t len = 0;
    uint8_t *frame = aethernet_webrtc_signal_frame_encode(&sig, &len);
    assert(frame);
    const char *body = (const char *)(frame + 4);

    assert(strstr(body, "\"FromUhid\":\"alice\"") != NULL);
    assert(strstr(body, "\"ToUhid\":\"bob\"")     != NULL);
    assert(strstr(body, "\"Type\":0")            != NULL);  /* numeric enum */
    assert(strstr(body, "\"Sdp\":")              != NULL);
    assert(strstr(body, "\"SdpMLineIndex\":0")   != NULL);  /* always present */
    assert(strstr(body, "\"Candidate\"")         == NULL);  /* omitted (null)  */
    assert(strstr(body, "\"SdpMid\"")            == NULL);  /* omitted (null)  */
    free(frame);
}

/* A CANDIDATE carries Candidate + SdpMid and OMITS Sdp — matching C#. */
static void candidate_json_body_matches_csharp_shape(void) {
    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof(sig));
    strcpy(sig.from_uhid, "alice");
    strcpy(sig.to_uhid, "bob");
    sig.type = AETHERNET_WEBRTC_SIGNAL_CANDIDATE;
    strcpy(sig.candidate, "candidate:1 1 UDP 1 10.0.0.1 5000 typ host");
    strcpy(sig.sdp_mid, "0");

    size_t len = 0;
    uint8_t *frame = aethernet_webrtc_signal_frame_encode(&sig, &len);
    assert(frame);
    const char *body = (const char *)(frame + 4);

    assert(strstr(body, "\"Type\":2")     != NULL);
    assert(strstr(body, "\"Candidate\":") != NULL);
    assert(strstr(body, "\"SdpMid\":\"0\"") != NULL);
    assert(strstr(body, "\"Sdp\"")        == NULL);   /* omitted for candidate */
    free(frame);
}

/* encode → decode is a faithful round-trip for every field the C struct holds. */
static void frame_encode_decode_round_trips(void) {
    aethernet_webrtc_signal_t in;
    memset(&in, 0, sizeof(in));
    strcpy(in.from_uhid, "node-A");
    strcpy(in.to_uhid, "node-B");
    in.type = AETHERNET_WEBRTC_SIGNAL_ANSWER;
    strcpy(in.sdp, "v=0\r\ns=answer");

    size_t len = 0;
    uint8_t *frame = aethernet_webrtc_signal_frame_encode(&in, &len);
    assert(frame);

    aethernet_webrtc_signal_t out;
    assert(aethernet_webrtc_signal_frame_decode(frame, len, &out));
    assert(strcmp(out.from_uhid, "node-A") == 0);
    assert(strcmp(out.to_uhid, "node-B")   == 0);
    assert(out.type == AETHERNET_WEBRTC_SIGNAL_ANSWER);
    assert(strcmp(out.sdp, "v=0\r\ns=answer") == 0);
    free(frame);
}

/* Bytes without the AWS1 magic decode as "not a signal" (false), never a
 * corrupt signal — the C# NonSignallingBytes_AreIgnored contract. */
static void non_magic_bytes_are_not_a_signal(void) {
    const uint8_t app[] = "ordinary app data";
    aethernet_webrtc_signal_t out;
    assert(!aethernet_webrtc_signal_frame_has_magic(app, sizeof(app) - 1));
    assert(!aethernet_webrtc_signal_frame_decode(app, sizeof(app) - 1, &out));

    /* Magic present but the body is not JSON => also false (discarded). */
    const uint8_t bad[] = { 'A', 'W', 'S', '1', '{', 'n', 'o', 'p', 'e' };
    assert(!aethernet_webrtc_signal_frame_decode(bad, sizeof(bad), &out));
}

/* ───────────────────────── tests: carrier over a transport pair ───────────*/

/* THE HEADLINE CARRIER TEST: two SEPARATE carrier instances (two nodes) over an
 * in-process loopback transport PAIR round-trip an OFFER and an ANSWER across
 * the transport boundary. Proves the carrier — send frames + wire delivery +
 * receive parse — end to end, exactly as C# RelaySignalingTests proves
 * RelayWebRtcSignaling over two wired LoopbackTransports. */
static void two_carriers_round_trip_offer_and_answer(void) {
    /* Two loopback endpoints wired to each other — the only shared channel. */
    aethernet_transport_t *alice_wire = loopback_new("alice");
    aethernet_transport_t *bob_wire   = loopback_new("bob");
    loopback_pair_wire(alice_wire, bob_wire);

    /* Two SEPARATE carrier instances, one per node, each over its own wire. */
    aethernet_webrtc_signaling_carrier_t *alice_carrier =
        aethernet_webrtc_signaling_carrier_new(alice_wire);
    aethernet_webrtc_signaling_carrier_t *bob_carrier =
        aethernet_webrtc_signaling_carrier_new(bob_wire);
    assert(alice_carrier && bob_carrier);

    aethernet_webrtc_signaling_t *alice_sig =
        aethernet_webrtc_signaling_carrier_iface(alice_carrier);
    aethernet_webrtc_signaling_t *bob_sig =
        aethernet_webrtc_signaling_carrier_iface(bob_carrier);
    assert(alice_sig && bob_sig);

    signal_sink_t at_bob, at_alice;
    memset(&at_bob, 0, sizeof(at_bob));
    memset(&at_alice, 0, sizeof(at_alice));
    bob_sig->set_handler(bob_sig->handle, on_signal, &at_bob);
    alice_sig->set_handler(alice_sig->handle, on_signal, &at_alice);

    /* alice --OFFER--> bob, over the transport. */
    aethernet_webrtc_signal_t offer;
    memset(&offer, 0, sizeof(offer));
    strcpy(offer.from_uhid, "alice");
    strcpy(offer.to_uhid, "bob");
    offer.type = AETHERNET_WEBRTC_SIGNAL_OFFER;
    strcpy(offer.sdp, "v=0\r\no=alice offer");
    assert(alice_sig->send(alice_sig->handle, &offer));

    assert(at_bob.count == 1);
    assert(at_bob.last.type == AETHERNET_WEBRTC_SIGNAL_OFFER);
    assert(strcmp(at_bob.last.from_uhid, "alice") == 0);
    assert(strcmp(at_bob.last.to_uhid, "bob")     == 0);
    assert(strcmp(at_bob.last.sdp, "v=0\r\no=alice offer") == 0);

    /* bob --ANSWER--> alice, over the transport (the reverse direction). */
    aethernet_webrtc_signal_t answer;
    memset(&answer, 0, sizeof(answer));
    strcpy(answer.from_uhid, "bob");
    strcpy(answer.to_uhid, "alice");
    answer.type = AETHERNET_WEBRTC_SIGNAL_ANSWER;
    strcpy(answer.sdp, "v=0\r\no=bob answer");
    assert(bob_sig->send(bob_sig->handle, &answer));

    assert(at_alice.count == 1);
    assert(at_alice.last.type == AETHERNET_WEBRTC_SIGNAL_ANSWER);
    assert(strcmp(at_alice.last.from_uhid, "bob")  == 0);
    assert(strcmp(at_alice.last.to_uhid, "alice")  == 0);
    assert(strcmp(at_alice.last.sdp, "v=0\r\no=bob answer") == 0);

    aethernet_webrtc_signaling_carrier_destroy(alice_carrier);
    aethernet_webrtc_signaling_carrier_destroy(bob_carrier);
    aethernet_transport_destroy(alice_wire);
    aethernet_transport_destroy(bob_wire);
}

/* App traffic (no AWS1 magic) pushed onto the wire must NOT surface as a signal
 * on the carrier — the C# NonSignallingBytes_AreIgnored behaviour, end to end. */
static void carrier_ignores_non_signalling_bytes(void) {
    aethernet_transport_t *a_wire = loopback_new("a");
    aethernet_transport_t *b_wire = loopback_new("b");
    loopback_pair_wire(a_wire, b_wire);

    aethernet_webrtc_signaling_carrier_t *a_carrier =
        aethernet_webrtc_signaling_carrier_new(a_wire);
    aethernet_webrtc_signaling_carrier_t *b_carrier =
        aethernet_webrtc_signaling_carrier_new(b_wire);

    signal_sink_t at_b;
    memset(&at_b, 0, sizeof(at_b));
    aethernet_webrtc_signaling_t *b_sig =
        aethernet_webrtc_signaling_carrier_iface(b_carrier);
    b_sig->set_handler(b_sig->handle, on_signal, &at_b);

    /* Drive plain bytes A->B directly on the transport (bypassing the carrier's
     * frame encoder), exactly like the C# test sends "ordinary app data". */
    const uint8_t app[] = "ordinary app data";
    assert(aethernet_transport_send(a_wire, "b", app, sizeof(app) - 1));
    assert(at_b.count == 0);  /* not decoded as a signal */

    aethernet_webrtc_signaling_carrier_destroy(a_carrier);
    aethernet_webrtc_signaling_carrier_destroy(b_carrier);
    aethernet_transport_destroy(a_wire);
    aethernet_transport_destroy(b_wire);
}

/* ───────────────────────── optional: full P2P over libdatachannel ─────────*/

#ifdef AETHERNET_WITH_WEBRTC
#include "aethernet/transport_webrtc.h"

typedef struct {
    pthread_mutex_t lock;
    pthread_cond_t  cv;
    int             count;
    uint8_t         buf[256];
    size_t          len;
} p2p_sink_t;

static void p2p_on_data(const char *from, const uint8_t *data, size_t len, void *ud) {
    (void)from;
    p2p_sink_t *s = (p2p_sink_t *)ud;
    pthread_mutex_lock(&s->lock);
    s->count++;
    if (len <= sizeof(s->buf)) { memcpy(s->buf, data, len); s->len = len; }
    pthread_cond_broadcast(&s->cv);
    pthread_mutex_unlock(&s->lock);
}

static bool p2p_wait(p2p_sink_t *s, long timeout_ms) {
    struct timespec dl;
    clock_gettime(CLOCK_REALTIME, &dl);
    dl.tv_sec  += timeout_ms / 1000;
    dl.tv_nsec += (timeout_ms % 1000) * 1000000L;
    if (dl.tv_nsec >= 1000000000L) { dl.tv_sec += 1; dl.tv_nsec -= 1000000000L; }
    pthread_mutex_lock(&s->lock);
    while (s->count == 0) {
        if (pthread_cond_timedwait(&s->cv, &s->lock, &dl) != 0) break;
    }
    bool got = s->count > 0;
    pthread_mutex_unlock(&s->lock);
    return got;
}

/* Full offer/answer: two real WebRTC transports, each driven by a carrier over
 * the loopback pair, negotiate a direct data channel over the CARRIED handshake
 * and move a byte payload peer-to-peer. Proves the carrier plugs into the real
 * signalling seam.
 *
 * NOTE: this uses the SYNCHRONOUS loopback pair, so a signal is delivered on the
 * sender's own libdatachannel callback thread. transport_webrtc.c starts each
 * handshake outside its transport lock precisely so re-entrant delivery is safe,
 * mirroring the C# LoopbackTransport-based RelaySignalingTests. */
static void full_handshake_over_carrier_pair(void) {
    aethernet_transport_t *alice_wire = loopback_new("alice");
    aethernet_transport_t *bob_wire   = loopback_new("bob");
    loopback_pair_wire(alice_wire, bob_wire);

    aethernet_webrtc_signaling_carrier_t *alice_carrier =
        aethernet_webrtc_signaling_carrier_new(alice_wire);
    aethernet_webrtc_signaling_carrier_t *bob_carrier =
        aethernet_webrtc_signaling_carrier_new(bob_wire);
    assert(alice_carrier && bob_carrier);

    aethernet_transport_t *alice = aethernet_webrtc_transport_new(
        "alice", aethernet_webrtc_signaling_carrier_iface(alice_carrier), NULL, 0);
    aethernet_transport_t *bob = aethernet_webrtc_transport_new(
        "bob", aethernet_webrtc_signaling_carrier_iface(bob_carrier), NULL, 0);
    assert(alice && bob);

    p2p_sink_t sink;
    memset(&sink, 0, sizeof(sink));
    pthread_mutex_init(&sink.lock, NULL);
    pthread_cond_init(&sink.cv, NULL);
    aethernet_transport_set_on_data_received(bob, p2p_on_data, &sink);

    const uint8_t payload[] = "handshake rode the carrier; the data went direct";
    size_t payload_len = sizeof(payload) - 1;
    bool ok = aethernet_transport_send(alice, "bob", payload, payload_len);
    assert(ok);

    bool got = p2p_wait(&sink, 30000);
    assert(got);
    assert(sink.len == payload_len);
    assert(memcmp(sink.buf, payload, payload_len) == 0);
    assert(aethernet_transport_is_connected(alice, "bob"));
    assert(aethernet_transport_is_connected(bob, "alice"));

    pthread_cond_destroy(&sink.cv);
    pthread_mutex_destroy(&sink.lock);
    aethernet_transport_destroy(alice);
    aethernet_transport_destroy(bob);
    aethernet_webrtc_signaling_carrier_destroy(alice_carrier);
    aethernet_webrtc_signaling_carrier_destroy(bob_carrier);
    aethernet_transport_destroy(alice_wire);
    aethernet_transport_destroy(bob_wire);
}
#endif /* AETHERNET_WITH_WEBRTC */

// ── main ─────────────────────────────────────────────────────

int main(void) {
    printf("Aether WebRTC Signalling Carrier — Acceptance Tests\n");
    printf("====================================================\n");

    RUN(frame_magic_is_AWS1);
    RUN(offer_json_body_matches_csharp_shape);
    RUN(candidate_json_body_matches_csharp_shape);
    RUN(frame_encode_decode_round_trips);
    RUN(non_magic_bytes_are_not_a_signal);
    RUN(two_carriers_round_trip_offer_and_answer);
    RUN(carrier_ignores_non_signalling_bytes);
#ifdef AETHERNET_WITH_WEBRTC
    RUN(full_handshake_over_carrier_pair);
    printf("\n[level] full offer/answer over libdatachannel (AETHERNET_WITH_WEBRTC=ON)\n");
#else
    printf("\n[level] carrier offer+answer round-trip over an in-process transport pair\n");
    printf("[level] (libdatachannel OFF: full P2P handshake test compiled out)\n");
#endif

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
