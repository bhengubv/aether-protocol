// SPDX-License-Identifier: MIT
// Unit tests for directory_service.c (DirectoryService).
//
// Mirrors AetherNet.Core.Tests/DirectoryServiceTests.cs (C# reference): publish
// stores locally, resolve returns local hit, inbound NamePublish fires the
// EntryAnnounced callback, inbound NameQuery for held name unicasts a response,
// inbound NameQuery for unknown name silently ignored. Wave-16 Issue #60.

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/directory_service.h"
#include "aethernet/protocol.h"

#define LOCAL_UHID "node-a"

// ───── FakeMeshSender (Directory flavour) ──────────────────────
//
// Captures every broadcast and unicast packet so tests can assert on the wire
// shape that a publish / query / response produced.

typedef struct {
    aethernet_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethernet_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
} fake_state_t;

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet,
                      const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aethernet_mesh_packet_t **)realloc(s->unicasts,
            sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops,
            sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aethernet_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethernet_mesh_packet_t **)realloc(s->broadcasts,
            sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethernet_packet_clone(packet);
    return 1;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethernet_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    memset(s, 0, sizeof(*s));
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

// Build a ContentDescriptor populated with deterministic values for assertions.
static aethernet_content_descriptor_t *make_test_descriptor(const char *name, const char *root_hash) {
    aethernet_content_descriptor_t *d = aethernet_content_descriptor_new();
    if (!d) return NULL;
    d->root_hash = strdup(root_hash);
    d->name = strdup(name);
    d->total_bytes = 1024;
    d->chunk_size_bytes = 256;
    d->chunk_count = 4;
    d->content_type = strdup("application/octet-stream");
    d->created_at = strdup("2026-06-07T00:00:00Z");
    d->chunk_hashes = (char **)calloc(4, sizeof(char *));
    d->chunk_hashes_count = 4;
    d->chunk_hashes[0] = strdup("aaaa");
    d->chunk_hashes[1] = strdup("bbbb");
    d->chunk_hashes[2] = strdup("cccc");
    d->chunk_hashes[3] = strdup("dddd");
    return d;
}

// Entry-announced cookie used by tests to capture callback arguments.
typedef struct {
    int call_count;
    char *last_name;
    char *last_source_uhid;
    char *last_root_hash;
} announce_cookie_t;

static void on_entry_announced(void *user_data, const char *name,
                               const aethernet_content_descriptor_t *desc,
                               const char *source_uhid) {
    announce_cookie_t *c = (announce_cookie_t *)user_data;
    c->call_count++;
    free(c->last_name);
    free(c->last_source_uhid);
    free(c->last_root_hash);
    c->last_name = name ? strdup(name) : NULL;
    c->last_source_uhid = source_uhid ? strdup(source_uhid) : NULL;
    c->last_root_hash = (desc && desc->root_hash) ? strdup(desc->root_hash) : NULL;
}

static void announce_cookie_clear(announce_cookie_t *c) {
    free(c->last_name);
    free(c->last_source_uhid);
    free(c->last_root_hash);
    memset(c, 0, sizeof(*c));
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Tests ─────────────────────────────────────────────

// (C) directory_publish stores locally + sends NamePublish.
static void publish_stores_locally_and_broadcasts(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    aethernet_content_descriptor_t *desc = make_test_descriptor("podcast:abc123", "deadbeef");
    assert(desc != NULL);

    int rc = aethernet_directory_publish(svc, "podcast:abc123", desc);
    assert(rc == 0);

    // Catalogue holds the binding now — list returns count = 1.
    int n = aethernet_directory_list_names(svc, NULL, 0);
    assert(n == 1);

    // Exactly one NamePublish broadcast was emitted.
    int publish_count = 0;
    for (int i = 0; i < s.broadcasts_len; i++) {
        if (s.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_PUBLISH) publish_count++;
    }
    assert(publish_count == 1);

    aethernet_content_descriptor_free(desc);
    aethernet_directory_service_free(svc);
    fake_clear(&s);
}

// (D) directory_resolve local-hit returns 0 immediately with the descriptor filled.
static void resolve_local_hit_returns_zero(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    aethernet_content_descriptor_t *desc = make_test_descriptor("album:foo/bar", "cafebabe");
    aethernet_directory_publish(svc, "album:foo/bar", desc);

    aethernet_content_descriptor_t out = {0};
    int rc = aethernet_directory_resolve(svc, "album:foo/bar", &out, 5);
    assert(rc == 0);  // local hit
    assert(out.root_hash != NULL);
    assert(strcmp(out.root_hash, "cafebabe") == 0);
    assert(out.total_bytes == 1024);
    assert(out.chunk_count == 4);
    assert(out.chunk_hashes_count == 4);
    assert(strcmp(out.chunk_hashes[0], "aaaa") == 0);

    // Free out's owned fields — same shape as aethernet_content_descriptor_free
    // but applied to a stack-allocated header.
    free(out.root_hash); free(out.name); free(out.content_type); free(out.created_at);
    for (int i = 0; i < out.chunk_hashes_count; i++) free(out.chunk_hashes[i]);
    free(out.chunk_hashes);

    aethernet_content_descriptor_free(desc);
    aethernet_directory_service_free(svc);
    fake_clear(&s);
}

// (E) directory_handle(NamePublish): stores in catalogue + fires entry_announced.
static void handle_name_publish_stores_and_fires_callback(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    announce_cookie_t cookie = {0};
    aethernet_directory_set_entry_announced_callback(svc, on_entry_announced, &cookie);

    // Build a NamePublish packet from a remote peer.
    aethernet_content_descriptor_t *desc = make_test_descriptor("reel:xyz", "feedface");

    // First we get the publish to broadcast by using a sibling service to
    // produce the byte-shaped payload — fewer JSON-encoding assumptions in the
    // test. Use a second directory service with a different sender.
    fake_state_t s_peer = {0};
    aethernet_mesh_sender_t peer_sender = make_sender(&s_peer);
    peer_sender.local_uhid = "node-b";
    aethernet_directory_service_t *peer_svc = NULL;
    aethernet_directory_service_init(&peer_svc, &peer_sender);
    aethernet_directory_publish(peer_svc, "reel:xyz", desc);

    // Grab the broadcast packet — that's the NamePublish on the wire.
    assert(s_peer.broadcasts_len >= 1);
    const aethernet_mesh_packet_t *publish_pkt = NULL;
    for (int i = 0; i < s_peer.broadcasts_len; i++) {
        if (s_peer.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_PUBLISH) {
            publish_pkt = s_peer.broadcasts[i];
            break;
        }
    }
    assert(publish_pkt != NULL);
    // The packet's source_uhid was set by peer_svc's publish to "node-b".
    assert(publish_pkt->source_uhid != NULL);
    assert(strcmp(publish_pkt->source_uhid, "node-b") == 0);

    // Now pump it through the local service.
    int rc = aethernet_directory_handle(svc, publish_pkt);
    assert(rc == 0);

    // Callback fired exactly once with the expected args.
    assert(cookie.call_count == 1);
    assert(cookie.last_name && strcmp(cookie.last_name, "reel:xyz") == 0);
    assert(cookie.last_source_uhid && strcmp(cookie.last_source_uhid, "node-b") == 0);
    assert(cookie.last_root_hash && strcmp(cookie.last_root_hash, "feedface") == 0);

    // Catalogue now contains the name.
    int n = aethernet_directory_list_names(svc, NULL, 0);
    assert(n == 1);

    announce_cookie_clear(&cookie);
    aethernet_content_descriptor_free(desc);
    aethernet_directory_service_free(peer_svc);
    aethernet_directory_service_free(svc);
    fake_clear(&s_peer);
    fake_clear(&s);
}

// (F) directory_handle(NameQuery, name held locally): unicasts NamePublish response.
static void handle_name_query_known_name_sends_response(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    // Local node holds "skin:dark" via publish.
    aethernet_content_descriptor_t *desc = make_test_descriptor("skin:dark", "0123abcd");
    aethernet_directory_publish(svc, "skin:dark", desc);

    // Clear broadcasts captured during the publish so we can spot the response
    // unicast distinctly.
    int broadcasts_before = s.broadcasts_len;
    int unicasts_before = s.unicasts_len;

    // Build a NameQuery packet to inject. Use a sibling service to construct
    // the wire bytes for the same reason as test (E).
    fake_state_t s_peer = {0};
    aethernet_mesh_sender_t peer_sender = make_sender(&s_peer);
    peer_sender.local_uhid = "querier";
    aethernet_directory_service_t *peer_svc = NULL;
    aethernet_directory_service_init(&peer_svc, &peer_sender);

    aethernet_content_descriptor_t dummy = {0};
    // Resolve on the peer triggers a NameQuery broadcast since "skin:dark"
    // isn't in the peer's catalogue.
    int rc = aethernet_directory_resolve(peer_svc, "skin:dark", &dummy, 5);
    assert(rc == 1);  // 1 = query broadcast

    const aethernet_mesh_packet_t *query_pkt = NULL;
    for (int i = 0; i < s_peer.broadcasts_len; i++) {
        if (s_peer.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_QUERY) {
            query_pkt = s_peer.broadcasts[i];
            break;
        }
    }
    assert(query_pkt != NULL);

    // Pump the query through the local svc.
    rc = aethernet_directory_handle(svc, query_pkt);
    assert(rc == 0);

    // Local svc should have unicast a NamePublish response back to "querier".
    int response_count = 0;
    for (int i = unicasts_before; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHERNET_PACKET_TYPE_NAME_PUBLISH
            && s.unicasts_next_hops[i] && strcmp(s.unicasts_next_hops[i], "querier") == 0) {
            response_count++;
        }
    }
    assert(response_count == 1);
    // No new broadcasts captured (response was unicast, not broadcast).
    assert(s.broadcasts_len == broadcasts_before);

    aethernet_content_descriptor_free(desc);
    aethernet_directory_service_free(peer_svc);
    aethernet_directory_service_free(svc);
    fake_clear(&s_peer);
    fake_clear(&s);
}

// (G) directory_handle(NameQuery, name unknown): no response packet emitted.
static void handle_name_query_unknown_name_no_response(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    // Build a NameQuery for a name the local svc has never seen.
    fake_state_t s_peer = {0};
    aethernet_mesh_sender_t peer_sender = make_sender(&s_peer);
    peer_sender.local_uhid = "querier";
    aethernet_directory_service_t *peer_svc = NULL;
    aethernet_directory_service_init(&peer_svc, &peer_sender);

    aethernet_content_descriptor_t dummy = {0};
    aethernet_directory_resolve(peer_svc, "unknown:name", &dummy, 5);

    const aethernet_mesh_packet_t *query_pkt = NULL;
    for (int i = 0; i < s_peer.broadcasts_len; i++) {
        if (s_peer.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_QUERY) {
            query_pkt = s_peer.broadcasts[i];
            break;
        }
    }
    assert(query_pkt != NULL);

    int broadcasts_before = s.broadcasts_len;
    int unicasts_before = s.unicasts_len;

    int rc = aethernet_directory_handle(svc, query_pkt);
    assert(rc == 0);  // silent ignore is success

    // No new unicasts or broadcasts.
    assert(s.broadcasts_len == broadcasts_before);
    assert(s.unicasts_len == unicasts_before);

    aethernet_directory_service_free(peer_svc);
    aethernet_directory_service_free(svc);
    fake_clear(&s_peer);
    fake_clear(&s);
}

// (H) directory_resolve for unknown name returns 1 (query broadcast) — caller
// then polls; this single-threaded impl does not block.
static void resolve_unknown_name_broadcasts_and_returns_one(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    assert(aethernet_directory_service_init(&svc, &sender) == 0);

    aethernet_content_descriptor_t out = {0};
    int rc = aethernet_directory_resolve(svc, "missing:name", &out, 1);
    assert(rc == 1);  // 1 = query broadcast, caller polls

    int query_count = 0;
    for (int i = 0; i < s.broadcasts_len; i++) {
        if (s.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_QUERY) query_count++;
    }
    assert(query_count == 1);

    aethernet_directory_service_free(svc);
    fake_clear(&s);
}

// (I) Query/response round-trip: resolve broadcasts query, peer answers via
// handle, polling resolve picks up the now-cached entry.
static void query_response_round_trip_populates_catalogue(void) {
    fake_state_t s_a = {0};
    aethernet_mesh_sender_t sender_a = make_sender(&s_a);
    sender_a.local_uhid = "node-a";
    aethernet_directory_service_t *svc_a = NULL;
    aethernet_directory_service_init(&svc_a, &sender_a);

    fake_state_t s_b = {0};
    aethernet_mesh_sender_t sender_b = make_sender(&s_b);
    sender_b.local_uhid = "node-b";
    aethernet_directory_service_t *svc_b = NULL;
    aethernet_directory_service_init(&svc_b, &sender_b);

    // node-b publishes "preset:vintage"
    aethernet_content_descriptor_t *desc = make_test_descriptor("preset:vintage", "cafe1234");
    aethernet_directory_publish(svc_b, "preset:vintage", desc);

    // node-a doesn't know about it — resolve triggers a query broadcast.
    aethernet_content_descriptor_t out = {0};
    int rc = aethernet_directory_resolve(svc_a, "preset:vintage", &out, 5);
    assert(rc == 1);  // broadcast triggered

    const aethernet_mesh_packet_t *query_pkt = NULL;
    for (int i = 0; i < s_a.broadcasts_len; i++) {
        if (s_a.broadcasts[i]->type == AETHERNET_PACKET_TYPE_NAME_QUERY) {
            query_pkt = s_a.broadcasts[i];
            break;
        }
    }
    assert(query_pkt != NULL);

    // node-b receives node-a's query and unicasts a response.
    aethernet_directory_handle(svc_b, query_pkt);

    // Find the response packet directed at "node-a".
    const aethernet_mesh_packet_t *response_pkt = NULL;
    for (int i = 0; i < s_b.unicasts_len; i++) {
        if (s_b.unicasts[i]->type == AETHERNET_PACKET_TYPE_NAME_PUBLISH
            && s_b.unicasts_next_hops[i] && strcmp(s_b.unicasts_next_hops[i], "node-a") == 0) {
            response_pkt = s_b.unicasts[i];
            break;
        }
    }
    assert(response_pkt != NULL);

    // node-a pumps the response — catalogue is populated, second resolve hits.
    aethernet_directory_handle(svc_a, response_pkt);

    aethernet_content_descriptor_t out2 = {0};
    rc = aethernet_directory_resolve(svc_a, "preset:vintage", &out2, 0);
    assert(rc == 0);  // local hit now
    assert(out2.root_hash && strcmp(out2.root_hash, "cafe1234") == 0);

    free(out2.root_hash); free(out2.name); free(out2.content_type); free(out2.created_at);
    for (int i = 0; i < out2.chunk_hashes_count; i++) free(out2.chunk_hashes[i]);
    free(out2.chunk_hashes);

    aethernet_content_descriptor_free(desc);
    aethernet_directory_service_free(svc_a);
    aethernet_directory_service_free(svc_b);
    fake_clear(&s_a);
    fake_clear(&s_b);
}

// (J) Non-directory packet type is silently ignored (returns 0, no state change).
static void handle_non_directory_packet_is_noop(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_directory_service_t *svc = NULL;
    aethernet_directory_service_init(&svc, &sender);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;  // unrelated type
    aethernet_packet_set_source_uhid(pkt, "stranger");

    int rc = aethernet_directory_handle(svc, pkt);
    assert(rc == 0);
    assert(aethernet_directory_list_names(svc, NULL, 0) == 0);

    aethernet_packet_free(pkt);
    aethernet_directory_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether DirectoryService - Unit Tests\n");
    printf("====================================\n");

    RUN(publish_stores_locally_and_broadcasts);
    RUN(resolve_local_hit_returns_zero);
    RUN(handle_name_publish_stores_and_fires_callback);
    RUN(handle_name_query_known_name_sends_response);
    RUN(handle_name_query_unknown_name_no_response);
    RUN(resolve_unknown_name_broadcasts_and_returns_one);
    RUN(query_response_round_trip_populates_catalogue);
    RUN(handle_non_directory_packet_is_noop);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
