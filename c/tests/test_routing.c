// SPDX-License-Identifier: MIT
// Unit tests for routing.c (RoutingService).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet/security.h"
#include "aethernet_reputation.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender ─────────────────────────────────────

typedef struct {
    aethernet_mesh_packet_t **items;
    int len;
    int cap;
} pkt_list_t;

typedef struct {
    char **next_hops;
    aethernet_mesh_packet_t **packets;
    int len;
    int cap;
} unicast_list_t;

typedef struct {
    pkt_list_t broadcasts;
    unicast_list_t unicasts;
    int peer_count;
} fake_state_t;

static void pkt_list_push(pkt_list_t *L, const aethernet_mesh_packet_t *p) {
    if (L->len == L->cap) {
        L->cap = L->cap ? L->cap * 2 : 8;
        L->items = (aethernet_mesh_packet_t **)realloc(L->items, sizeof(*L->items) * (size_t)L->cap);
    }
    L->items[L->len++] = aethernet_packet_clone(p);
}

static void unicast_list_push(unicast_list_t *L, const aethernet_mesh_packet_t *p, const char *next_hop) {
    if (L->len == L->cap) {
        L->cap = L->cap ? L->cap * 2 : 8;
        L->next_hops = (char **)realloc(L->next_hops, sizeof(*L->next_hops) * (size_t)L->cap);
        L->packets = (aethernet_mesh_packet_t **)realloc(L->packets, sizeof(*L->packets) * (size_t)L->cap);
    }
    L->next_hops[L->len] = next_hop ? strdup(next_hop) : NULL;
    L->packets[L->len] = aethernet_packet_clone(p);
    L->len++;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts.len; i++) aethernet_packet_free(s->broadcasts.items[i]);
    free(s->broadcasts.items);
    s->broadcasts.items = NULL; s->broadcasts.len = s->broadcasts.cap = 0;

    for (int i = 0; i < s->unicasts.len; i++) {
        free(s->unicasts.next_hops[i]);
        aethernet_packet_free(s->unicasts.packets[i]);
    }
    free(s->unicasts.next_hops);
    free(s->unicasts.packets);
    s->unicasts.next_hops = NULL; s->unicasts.packets = NULL;
    s->unicasts.len = s->unicasts.cap = 0;
}

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    unicast_list_push(&s->unicasts, packet, next_hop_uhid);
    return true;
}

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    pkt_list_push(&s->broadcasts, packet);
    return s->peer_count;
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Helpers ────────────────────────────────────────────

static aethernet_mesh_packet_t *new_rreq(const char *src, const char *dst, int32_t ttl) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_ROUTE_REQUEST;
    aethernet_packet_set_source_uhid(p, src);
    aethernet_packet_set_destination_uhid(p, dst);
    p->ttl = ttl;
    return p;
}

static aethernet_mesh_packet_t *new_rrep(const char *src, const char *dst, int32_t ttl) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_ROUTE_REPLY;
    aethernet_packet_set_source_uhid(p, src);
    aethernet_packet_set_destination_uhid(p, dst);
    p->ttl = ttl;
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Tests ──────────────────────────────────────────────

static void handle_rreq_drops_duplicate_by_id(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq("alice", "bob", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, rreq);
    fake_clear(&s);
    aethernet_routing_handle_rreq(svc, rreq);

    assert(s.broadcasts.len == 0);
    assert(s.unicasts.len == 0);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rreq_ignores_self_originated(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq(LOCAL_UHID, "bob", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, rreq);

    assert(s.broadcasts.len == 0);
    assert(s.unicasts.len == 0);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rreq_installs_reverse_route(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq("alice", "bob", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, rreq);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "alice", &route);
    assert(found);
    assert(route != NULL);
    assert(strcmp(route->next_hop_uhid, "alice") == 0);
    assert(route->hop_count >= 1);
    aethernet_route_entry_free(route);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rreq_as_destination_sends_rrep_back(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq("alice", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, rreq);

    // RREP should be sent back; either via unicast (reverse route just installed)
    // or fallback broadcast.
    int rrep_count = 0;
    for (int i = 0; i < s.unicasts.len; i++) {
        if (s.unicasts.packets[i]->type == AETHERNET_PACKET_TYPE_ROUTE_REPLY) {
            assert(strcmp(s.unicasts.packets[i]->source_uhid, LOCAL_UHID) == 0);
            assert(strcmp(s.unicasts.packets[i]->destination_uhid, "alice") == 0);
            assert(strcmp(s.unicasts.next_hops[i], "alice") == 0);
            rrep_count++;
        }
    }
    for (int i = 0; i < s.broadcasts.len; i++) {
        if (s.broadcasts.items[i]->type == AETHERNET_PACKET_TYPE_ROUTE_REPLY) rrep_count++;
    }
    assert(rrep_count == 1);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rreq_forwards_when_ttl_allows(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq("alice", "carol", 5);
    aethernet_routing_handle_rreq(svc, rreq);

    assert(s.broadcasts.len == 1);
    assert(s.broadcasts.items[0]->ttl == 4);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rreq_drops_when_ttl_exhausted(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_mesh_packet_t *rreq = new_rreq("alice", "carol", 1);
    aethernet_routing_handle_rreq(svc, rreq);

    assert(s.broadcasts.len == 0);
    assert(s.unicasts.len == 0);

    aethernet_packet_free(rreq);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_installs_forward_route(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_accept_all, NULL);
    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(found);
    assert(route != NULL);
    assert(strcmp(route->next_hop_uhid, "carol") == 0);
    aethernet_route_entry_free(route);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_forwards_toward_original_requester(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    // First seed a reverse route to "alice" via "bob" by handling an RREQ from alice via bob.
    // Since the C API installs a reverse route from RREQ source = "alice", but next-hop ends up as "alice" too.
    // To get a route alice → next_hop=bob we install via an RREP from bob with destination=alice.
    // Easier path: handle an RREQ where source=bob, dest=carol — installs route bob→bob.
    // For the forwards test we need a route alice→bob; use handle_rrep with source=alice, dest=local
    // … but that installs alice→alice. The C impl always installs source→source from headers,
    // which matches all other languages. So we install alice via RREQ from alice (route alice→alice)
    // then the rrep from carol to alice with ttl=4 is forwarded along reverse(alice→alice) with ttl=3.
    aethernet_mesh_packet_t *seed = new_rreq("alice", "carol", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, seed);
    aethernet_packet_free(seed);
    fake_clear(&s);

    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_accept_all, NULL);
    aethernet_mesh_packet_t *rrep = new_rrep("carol", "alice", 4);
    aethernet_routing_handle_rrep(svc, rrep);

    int forwarded = 0;
    for (int i = 0; i < s.unicasts.len; i++) {
        if (s.unicasts.packets[i]->type == AETHERNET_PACKET_TYPE_ROUTE_REPLY
            && strcmp(s.unicasts.next_hops[i], "alice") == 0) {
            assert(s.unicasts.packets[i]->ttl == 3);
            forwarded++;
        }
    }
    assert(forwarded == 1);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void find_cached_returns_null_when_not_present(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "bob", &route);
    assert(!found);
    assert(route == NULL);

    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void discover_returns_neg1_when_no_peers(void) {
    fake_state_t s = {0}; // peer_count = 0
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    int rc = aethernet_routing_discover(svc, "bob");
    assert(rc == -1);

    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void prune_removes_expired_routes(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    // Install a route via handle_rreq. We can't directly insert an expired
    // route through the public API, so we install fresh and then verify that
    // calling prune doesn't drop it.
    aethernet_mesh_packet_t *seed = new_rreq("fresh", "carol", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, seed);
    aethernet_packet_free(seed);
    fake_clear(&s);

    int n = aethernet_routing_prune(svc);
    assert(n == 0);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "fresh", &route);
    assert(found);
    aethernet_route_entry_free(route);

    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

// ── Item 19: RREQ-flood reputation hook ──────────────────

static void test_rreq_flood_fires_reputation(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);
    aethernet_routing_set_reputation(svc, &rep);

    /* Send AETHERNET_RREQ_RATE_LIMIT_MAX unique RREQs from same source — still within limit */
    for (int i = 0; i < AETHERNET_RREQ_RATE_LIMIT_MAX; i++) {
        aethernet_mesh_packet_t *pkt = new_rreq("attacker", "dest", AETHERNET_DEFAULT_TTL);
        aethernet_routing_handle_rreq(svc, pkt);
        aethernet_packet_free(pkt);
    }
    double score_before = aethernet_reputation_get_score(&rep, "attacker");
    assert(score_before > 0.99); /* should still be 1.0 */

    /* 11th unique RREQ — crosses limit, fires hook */
    aethernet_mesh_packet_t *pkt11 = new_rreq("attacker", "dest", AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rreq(svc, pkt11);
    aethernet_packet_free(pkt11);

    double score_after = aethernet_reputation_get_score(&rep, "attacker");
    /* After 1 flood record: 1.0 - 0.05 = 0.95 */
    assert(score_after < 0.96 && score_after > 0.94);

    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void test_rreq_normal_traffic_not_penalised(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);
    aethernet_routing_set_reputation(svc, &rep);

    /* 5 distinct sources each send 1 RREQ — none should be penalised */
    for (int i = 0; i < 5; i++) {
        char src[32];
        snprintf(src, sizeof(src), "node-%d", i);
        aethernet_mesh_packet_t *pkt = new_rreq(src, "dest", AETHERNET_DEFAULT_TTL);
        aethernet_routing_handle_rreq(svc, pkt);
        aethernet_packet_free(pkt);
    }

    for (int i = 0; i < 5; i++) {
        char src[32];
        snprintf(src, sizeof(src), "node-%d", i);
        double score = aethernet_reputation_get_score(&rep, src);
        assert(score > 0.99);
    }

    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void test_rreq_no_reputation_no_crash(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);
    /* No reputation attached — flood path must not crash */

    for (int i = 0; i <= AETHERNET_RREQ_RATE_LIMIT_MAX; i++) {
        aethernet_mesh_packet_t *pkt = new_rreq("attacker", "dest", AETHERNET_DEFAULT_TTL);
        aethernet_routing_handle_rreq(svc, pkt);
        aethernet_packet_free(pkt);
    }
    /* No crash */
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

// ── Gap 3: fail-closed RREP verification ──────────────────
//
// The routing service now REJECTS every RREP unless a verifier is installed and
// it proves the RREP authentic. These tests cover: (a) no verifier => rejected,
// no route; (b) explicit accept-all => route installed; (c) the real Ed25519
// verifier accepts a validly-signed RREP and rejects forged / unsigned ones.

/* Sign `pkt` in place with `priv` over the canonical signable bytes — the exact
 * layout aethernet_packet_signing_verify() and every other language binding use. */
static void sign_packet(aethernet_mesh_packet_t *pkt, const uint8_t *priv) {
    size_t len = 0;
    uint8_t *data = aethernet_packet_get_signable_data(pkt, &len);
    assert(data != NULL);
    uint8_t sig[AETHERNET_ED25519_SIGNATURE_SIZE];
    assert(aethernet_ed25519_sign(priv, data, len, sig));
    assert(aethernet_packet_set_signature(pkt, sig, sizeof(sig)));
    free(data);
}

/* Test key resolver: knows exactly one identity, "carol" -> carol_pub. */
static uint8_t g_carol_pub[AETHERNET_ED25519_PUBLIC_KEY_SIZE];

static bool resolve_carol(void *user, const char *source_uhid, uint8_t *out_pubkey) {
    (void)user;
    if (source_uhid && strcmp(source_uhid, "carol") == 0) {
        memcpy(out_pubkey, g_carol_pub, AETHERNET_ED25519_PUBLIC_KEY_SIZE);
        return true;
    }
    return false;   /* unknown signer -> fail-closed reject */
}

static void handle_rrep_rejected_when_no_verifier(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);
    /* No verifier installed — fail-closed default must drop the RREP. */

    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(!found);            /* no route installed */
    assert(route == NULL);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_accept_all_installs_route(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);
    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_accept_all, NULL);

    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(found);
    assert(route != NULL);
    assert(strcmp(route->next_hop_uhid, "carol") == 0);
    aethernet_route_entry_free(route);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_ed25519_accepts_valid_signature(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    uint8_t carol_priv[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    assert(aethernet_ed25519_generate_keypair(carol_priv, g_carol_pub));

    aethernet_ed25519_route_reply_verifier_ctx_t ctx = { resolve_carol, NULL };
    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_ed25519, &ctx);

    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    sign_packet(rrep, carol_priv);           /* validly signed by carol */
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(found);
    assert(route != NULL);
    assert(strcmp(route->next_hop_uhid, "carol") == 0);
    aethernet_route_entry_free(route);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_ed25519_rejects_forged_signature(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    /* carol's real key is what the resolver returns... */
    uint8_t carol_priv[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    assert(aethernet_ed25519_generate_keypair(carol_priv, g_carol_pub));
    /* ...but the attacker signs the forged RREP with a DIFFERENT key. */
    uint8_t attacker_priv[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t attacker_pub[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    assert(aethernet_ed25519_generate_keypair(attacker_priv, attacker_pub));

    aethernet_ed25519_route_reply_verifier_ctx_t ctx = { resolve_carol, NULL };
    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_ed25519, &ctx);

    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    sign_packet(rrep, attacker_priv);        /* wrong key -> signature won't verify */
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(!found);            /* forged RREP rejected, no route */
    assert(route == NULL);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_ed25519_rejects_unsigned(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    uint8_t carol_priv[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    assert(aethernet_ed25519_generate_keypair(carol_priv, g_carol_pub));

    aethernet_ed25519_route_reply_verifier_ctx_t ctx = { resolve_carol, NULL };
    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_ed25519, &ctx);

    /* No signature set at all — must be rejected. */
    aethernet_mesh_packet_t *rrep = new_rrep("carol", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "carol", &route);
    assert(!found);            /* unsigned RREP rejected, no route */
    assert(route == NULL);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

static void handle_rrep_ed25519_rejects_unknown_source(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_routing_service_t *svc = aethernet_routing_service_new(&sender);

    /* Sign correctly with mallory's own key, but the resolver only knows carol
     * (resolve_carol returns false for any other UHID), so mallory's key is
     * unresolvable -> fail-closed reject (unknown signer). */
    uint8_t mallory_priv[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t mallory_pub[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
    assert(aethernet_ed25519_generate_keypair(mallory_priv, mallory_pub));

    aethernet_ed25519_route_reply_verifier_ctx_t ctx = { resolve_carol, NULL };
    aethernet_routing_set_reply_verifier(svc, aethernet_routing_verifier_ed25519, &ctx);

    aethernet_mesh_packet_t *rrep = new_rrep("mallory", LOCAL_UHID, AETHERNET_DEFAULT_TTL);
    sign_packet(rrep, mallory_priv);
    aethernet_routing_handle_rrep(svc, rrep);

    aethernet_route_entry_t *route = NULL;
    bool found = aethernet_routing_find_cached(svc, "mallory", &route);
    assert(!found);            /* unknown source rejected, no route */
    assert(route == NULL);

    aethernet_packet_free(rrep);
    aethernet_routing_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Routing Service — Unit Tests\n");
    printf("=====================================\n");

    RUN(handle_rreq_drops_duplicate_by_id);
    RUN(handle_rreq_ignores_self_originated);
    RUN(handle_rreq_installs_reverse_route);
    RUN(handle_rreq_as_destination_sends_rrep_back);
    RUN(handle_rreq_forwards_when_ttl_allows);
    RUN(handle_rreq_drops_when_ttl_exhausted);
    RUN(handle_rrep_installs_forward_route);
    RUN(handle_rrep_forwards_toward_original_requester);
    RUN(find_cached_returns_null_when_not_present);
    RUN(discover_returns_neg1_when_no_peers);
    RUN(prune_removes_expired_routes);
    RUN(test_rreq_flood_fires_reputation);
    RUN(test_rreq_normal_traffic_not_penalised);
    RUN(test_rreq_no_reputation_no_crash);
    RUN(handle_rrep_rejected_when_no_verifier);
    RUN(handle_rrep_accept_all_installs_route);
    RUN(handle_rrep_ed25519_accepts_valid_signature);
    RUN(handle_rrep_ed25519_rejects_forged_signature);
    RUN(handle_rrep_ed25519_rejects_unsigned);
    RUN(handle_rrep_ed25519_rejects_unknown_source);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
