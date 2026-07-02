// SPDX-License-Identifier: MIT
// Unit tests for profiles.c (ProfileService, PacketType 23).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/profiles.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────
// Profiles are exchanged DIRECTED (unicast) — the fake captures sender->send calls (packet + next
// hop), mirroring the C# FakeMeshSender.Sends list. broadcast is unused here.

typedef struct {
    aethernet_mesh_packet_t **sends;
    char **sends_next_hops;
    int sends_len;
    int sends_cap;
} fake_state_t;

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->sends_len == s->sends_cap) {
        s->sends_cap = s->sends_cap ? s->sends_cap * 2 : 8;
        s->sends = (aethernet_mesh_packet_t **)realloc(s->sends, sizeof(*s->sends) * (size_t)s->sends_cap);
        s->sends_next_hops = (char **)realloc(s->sends_next_hops, sizeof(*s->sends_next_hops) * (size_t)s->sends_cap);
    }
    s->sends[s->sends_len] = aethernet_packet_clone(packet);
    s->sends_next_hops[s->sends_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->sends_len++;
    return true;  // mirror the C# FakeMeshSender: SendAsync returns true
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->sends_len; i++) {
        aethernet_packet_free(s->sends[i]);
        free(s->sends_next_hops[i]);
    }
    free(s->sends);
    free(s->sends_next_hops);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state, const char *local_uhid) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = local_uhid;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = NULL;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

// Build a ProfileSync packet from `uhid` carrying the canonical payload, using the same public
// serializer the wire path uses.
static aethernet_mesh_packet_t *profile_packet(const char *uhid, const char *name, const char *avatar,
                                               const char *status, int64_t updated_at_ms) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_PROFILE_SYNC;
    aethernet_packet_set_source_uhid(p, uhid);
    aethernet_packet_set_destination_uhid(p, LOCAL_UHID);
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_profile_payload_serialize(uhid, name, avatar, status, updated_at_ms,
                                                  &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    char uhid[64];
    char display_name[64];
    char avatar_ref[64];
    char status_message[64];
    int64_t updated_at_ms;
} upd_capture_t;

static void on_updated(const aethernet_profile_t *p, void *ud) {
    upd_capture_t *c = (upd_capture_t *)ud;
    c->count++;
    snprintf(c->uhid, sizeof(c->uhid), "%s", p->uhid ? p->uhid : "");
    snprintf(c->display_name, sizeof(c->display_name), "%s", p->display_name ? p->display_name : "");
    snprintf(c->avatar_ref, sizeof(c->avatar_ref), "%s", p->avatar_ref ? p->avatar_ref : "");
    snprintf(c->status_message, sizeof(c->status_message), "%s", p->status_message ? p->status_message : "");
    c->updated_at_ms = p->updated_at_ms;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: aethernet_profile_payload_serialize must emit exactly the canonical bytes from
// fixtures/profiles/vectors.json for every language SDK.
static void payload_serializes_to_canonical_bytes(void) {
    // Vector "basic"
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_profile_payload_serialize(
        "aether:alice:01", "Alice", "blake3:abc", "available", 1700000000000LL, &json, &len));
    const char *expected1 =
        "{\"uhid\":\"aether:alice:01\",\"display_name\":\"Alice\",\"avatar_ref\":\"blake3:abc\","
        "\"status_message\":\"available\",\"updated_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected1));
    assert(memcmp(json, expected1, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);

    // Vector "minimal": empty string fields, updated_at_ms 0
    json = NULL; len = 0;
    assert(aethernet_profile_payload_serialize("n", "", "", "", 0, &json, &len));
    const char *expected2 =
        "{\"uhid\":\"n\",\"display_name\":\"\",\"avatar_ref\":\"\",\"status_message\":\"\",\"updated_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);

    // NULL string args are treated as empty (mirrors the C# `?? string.Empty`).
    json = NULL; len = 0;
    assert(aethernet_profile_payload_serialize("n", NULL, NULL, NULL, 0, &json, &len));
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);
}

// Publishing sends the local profile directly (unicast) to the peer — via send, not broadcast.
// Mirrors PublishProfileTo_SendsDirectedProfileToPeer.
static void publish_sends_directed_profile_to_peer(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);
    aethernet_profile_set_local(svc, "Alice", "blake3:abc", "available");

    bool ok = aethernet_profile_publish_to(svc, "aether:bob:02");
    assert(ok);

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_PROFILE_SYNC);
    assert(strcmp(s.sends_next_hops[0], "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:alice:01") == 0);
    // Body carries our uhid and display name.
    assert(strstr((const char *)s.sends[0]->payload, "\"uhid\":\"aether:alice:01\"") != NULL);
    assert(strstr((const char *)s.sends[0]->payload, "\"display_name\":\"Alice\"") != NULL);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// Handling a peer profile caches it (keyed by uhid) and fires the profile-updated callback with the
// decoded fields. Mirrors Handle_CachesPeerProfileAndRaisesEvent.
static void handle_caches_peer_profile_and_fires(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    upd_capture_t cap = {0};
    aethernet_profile_set_updated_cb(svc, on_updated, &cap);

    aethernet_mesh_packet_t *pkt =
        profile_packet("aether:bob:02", "Bob", "blake3:xyz", "busy", 1700000000000LL);
    bool ok = aethernet_profile_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);
    assert(cap.count == 1);
    assert(strcmp(cap.display_name, "Bob") == 0);

    const aethernet_profile_t *cached = aethernet_profile_get(svc, "aether:bob:02");
    assert(cached != NULL);
    assert(strcmp(cached->status_message, "busy") == 0);
    assert(strcmp(cached->avatar_ref, "blake3:xyz") == 0);
    assert(cached->updated_at_ms == 1700000000000LL);

    aethernet_profile_t *known = NULL;
    int n = 0;
    assert(aethernet_profile_get_known(svc, &known, &n) == 1);
    assert(n == 1);
    assert(strcmp(known[0].uhid, "aether:bob:02") == 0);
    aethernet_profile_list_free(known, n);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// A second profile from the same uhid refreshes the single cached record (no duplicate). Mirrors
// Handle_RefreshesExistingProfile.
static void handle_refreshes_existing_profile(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    aethernet_mesh_packet_t *p1 = profile_packet("aether:bob:02", "Bob", "", "here", 1000LL);
    aethernet_mesh_packet_t *p2 = profile_packet("aether:bob:02", "Bob", "", "away", 2000LL);
    assert(aethernet_profile_handle_packet(svc, p1));
    assert(aethernet_profile_handle_packet(svc, p2));
    aethernet_packet_free(p1);
    aethernet_packet_free(p2);

    const aethernet_profile_t *cached = aethernet_profile_get(svc, "aether:bob:02");
    assert(cached != NULL);
    assert(strcmp(cached->status_message, "away") == 0);
    assert(cached->updated_at_ms == 2000LL);

    aethernet_profile_t *known = NULL;
    int n = 0;
    assert(aethernet_profile_get_known(svc, &known, &n) == 1);
    assert(n == 1);  // still a single cached record
    aethernet_profile_list_free(known, n);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// Our own profile echoed back is ignored (false, nothing cached). Mirrors Handle_OwnProfile_IsIgnored.
static void handle_own_profile_is_ignored(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    aethernet_mesh_packet_t *pkt = profile_packet(LOCAL_UHID, "Me", "", "", 1LL);
    bool ok = aethernet_profile_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);
    assert(!ok);

    aethernet_profile_t *known = NULL;
    int n = 0;
    assert(aethernet_profile_get_known(svc, &known, &n) == 0);
    assert(n == 0);
    aethernet_profile_list_free(known, n);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false). Mirrors Handle_WrongPacketType_ReturnsFalse.
static void handle_wrong_packet_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    aethernet_mesh_packet_t *pkt = profile_packet("aether:bob:02", "Bob", "", "", 1LL);
    pkt->type = AETHERNET_PACKET_TYPE_DATA;  // not a ProfileSync
    assert(aethernet_profile_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// A malformed payload is a benign drop (false). Mirrors the C# HandleAsync JsonException path.
static void handle_malformed_payload_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_PROFILE_SYNC;
    aethernet_packet_set_source_uhid(pkt, "aether:bob:02");
    aethernet_packet_set_destination_uhid(pkt, LOCAL_UHID);
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(pkt, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_profile_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

// set-local / get-local round-trip: uhid comes from the sender, fields are stored, timestamp stamped.
static void set_and_get_local_profile(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_profile_service_t *svc = aethernet_profile_service_new(&sender);

    // Default local profile: uhid from sender, empty fields.
    const aethernet_profile_t *before = aethernet_profile_get_local(svc);
    assert(before != NULL);
    assert(strcmp(before->uhid, "aether:alice:01") == 0);
    assert(strcmp(before->display_name, "") == 0);

    aethernet_profile_set_local(svc, "Alice", "blake3:abc", "available");
    const aethernet_profile_t *after = aethernet_profile_get_local(svc);
    assert(after != NULL);
    assert(strcmp(after->uhid, "aether:alice:01") == 0);
    assert(strcmp(after->display_name, "Alice") == 0);
    assert(strcmp(after->avatar_ref, "blake3:abc") == 0);
    assert(strcmp(after->status_message, "available") == 0);
    assert(after->updated_at_ms > 0);  // stamped to now

    aethernet_profile_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Profile Service — Unit Tests\n");
    printf("===================================\n");

    RUN(payload_serializes_to_canonical_bytes);
    RUN(publish_sends_directed_profile_to_peer);
    RUN(handle_caches_peer_profile_and_fires);
    RUN(handle_refreshes_existing_profile);
    RUN(handle_own_profile_is_ignored);
    RUN(handle_wrong_packet_type_returns_false);
    RUN(handle_malformed_payload_returns_false);
    RUN(set_and_get_local_profile);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
