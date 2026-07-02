// SPDX-License-Identifier: MIT
// Directed peer-profile metadata exchange for the Aether mesh (PacketType 23).
//
// Single-threaded reference impl; hosts pumping packets from multiple threads
// must wrap the service in their own mutex. The ProfileSyncPayload is encoded
// with snprintf (byte-identical to the C# System.Text.Json SnakeCaseLower output —
// {"uhid":...,"display_name":...,"avatar_ref":...,"status_message":...,"updated_at_ms":M},
// no whitespace, all string fields always present) and decoded on receive with the
// vendored cJSON, matching the SOS / heartbeat approach. Profiles are exchanged
// DIRECTED (point-to-point via sender->send), NOT broadcast, so display names are
// not leaked to every device in range. Received profiles are cached (keyed by uhid)
// and surfaced via the profile-updated callback; our own uhid echoed back is ignored.

#include "aethernet/profiles.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

// A cached peer profile, keyed by uhid, upserted on each received ProfileSync (mirrors the C#
// ConcurrentDictionary<string, ProfileSyncPayload> _peerProfiles).
typedef struct profile_node {
    aethernet_profile_t profile;  // owns all its string fields
    struct profile_node *next;
} profile_node_t;

struct aethernet_profile_service {
    aethernet_mesh_sender_t *sender;
    aethernet_profile_t local;    // owns its string fields
    profile_node_t *peers;

    aethernet_profile_updated_cb updated_cb;
    void *updated_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_prof(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

// Duplicate `s`, mapping NULL to an empty owned string (the wire contract: string fields are always
// present, never null). Returns NULL only on allocation failure.
static char *str_dup_or_empty_prof(const char *s) {
    if (!s) s = "";
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// Free the owned string fields of a profile (does not free the profile struct itself).
static void profile_fields_free(aethernet_profile_t *p) {
    free(p->uhid);
    free(p->display_name);
    free(p->avatar_ref);
    free(p->status_message);
    p->uhid = NULL;
    p->display_name = NULL;
    p->avatar_ref = NULL;
    p->status_message = NULL;
}

// Populate `dst`'s owned string fields from the given values (mapping NULL→""), then scalars. On any
// allocation failure, frees whatever was allocated and returns false, leaving dst's pointers NULL.
static bool profile_fields_set(aethernet_profile_t *dst,
                               const char *uhid,
                               const char *display_name,
                               const char *avatar_ref,
                               const char *status_message,
                               int64_t updated_at_ms) {
    char *u = str_dup_or_empty_prof(uhid);
    char *d = str_dup_or_empty_prof(display_name);
    char *a = str_dup_or_empty_prof(avatar_ref);
    char *s = str_dup_or_empty_prof(status_message);
    if (!u || !d || !a || !s) {
        free(u); free(d); free(a); free(s);
        return false;
    }
    dst->uhid = u;
    dst->display_name = d;
    dst->avatar_ref = a;
    dst->status_message = s;
    dst->updated_at_ms = updated_at_ms;
    return true;
}

static profile_node_t *peer_find_prof(aethernet_profile_service_t *svc, const char *uhid) {
    for (profile_node_t *n = svc->peers; n; n = n->next) {
        if (n->profile.uhid && strcmp(n->profile.uhid, uhid) == 0) return n;
    }
    return NULL;
}

// Encode a ProfileSyncPayload as canonical JSON. snake_case keys, field order uhid, display_name,
// avatar_ref, status_message, updated_at_ms, no whitespace, all string fields present — the
// byte-identity gate (fixtures/profiles/vectors.json). Mirrors the C# reference (System.Text.Json,
// SnakeCaseLower). String fields are interpolated verbatim (as sos.c does for message/geohash), so
// the ASCII vectors reproduce byte-for-byte. NULL string args map to empty. We format directly
// rather than via cJSON's printer so the bytes carry no printer-inserted spacing.
static bool encode_profile_payload(const char *uhid,
                                   const char *display_name,
                                   const char *avatar_ref,
                                   const char *status_message,
                                   int64_t updated_at_ms,
                                   uint8_t **out_payload,
                                   uint32_t *out_len) {
    const char *u = uhid ? uhid : "";
    const char *d = display_name ? display_name : "";
    const char *a = avatar_ref ? avatar_ref : "";
    const char *s = status_message ? status_message : "";

    // int64 (<=20 digits, incl. sign) + the four variable strings + fixed key/punctuation. 128
    // covers the keys/int; add the string lengths.
    size_t cap = 128 + strlen(u) + strlen(d) + strlen(a) + strlen(s);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"uhid\":\"%s\",\"display_name\":\"%s\",\"avatar_ref\":\"%s\","
        "\"status_message\":\"%s\",\"updated_at_ms\":%lld}",
        u, d, a, s, (long long)updated_at_ms);

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// Public wrapper over encode_profile_payload — see aethernet/profiles.h. Kept thin so the wire path
// (aethernet_profile_publish_to) and the byte-identity gate exercise identical serialization.
bool aethernet_profile_payload_serialize(const char *uhid,
                                         const char *display_name,
                                         const char *avatar_ref,
                                         const char *status_message,
                                         int64_t updated_at_ms,
                                         uint8_t **out_json,
                                         uint32_t *out_len) {
    if (!out_json || !out_len) return false;
    return encode_profile_payload(uhid, display_name, avatar_ref, status_message, updated_at_ms,
                                  out_json, out_len);
}

// ─── Public API ──────────────────────────────────────────

aethernet_profile_service_t *aethernet_profile_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_profile_service_t *svc =
        (aethernet_profile_service_t *)calloc(1, sizeof(aethernet_profile_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    // Local profile defaults to the sender's uhid with empty fields (mirrors the C# ctor
    // `new ProfileSyncPayload { Uhid = sender.LocalUhid }`).
    if (!profile_fields_set(&svc->local, sender->local_uhid, NULL, NULL, NULL, 0)) {
        free(svc);
        return NULL;
    }
    return svc;
}

void aethernet_profile_service_free(aethernet_profile_service_t *service) {
    if (!service) return;
    profile_fields_free(&service->local);
    while (service->peers) {
        profile_node_t *next = service->peers->next;
        profile_fields_free(&service->peers->profile);
        free(service->peers);
        service->peers = next;
    }
    free(service);
}

void aethernet_profile_set_local(aethernet_profile_service_t *service,
                                 const char *display_name,
                                 const char *avatar_ref,
                                 const char *status_message) {
    if (!service) return;
    aethernet_profile_t next = {0};
    if (!profile_fields_set(&next, service->sender->local_uhid,
                            display_name, avatar_ref, status_message, now_ms_prof())) {
        return;  // OOM — leave the existing local profile intact
    }
    profile_fields_free(&service->local);
    service->local = next;
}

const aethernet_profile_t *aethernet_profile_get_local(const aethernet_profile_service_t *service) {
    if (!service) return NULL;
    return &service->local;
}

bool aethernet_profile_publish_to(aethernet_profile_service_t *service, const char *peer_uhid) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0') return false;
    if (!service->sender->send) return false;  // host wired no directed send — best-effort no-op

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_profile_payload(service->local.uhid, service->local.display_name,
                                service->local.avatar_ref, service->local.status_message,
                                service->local.updated_at_ms, &body, &body_len)) {
        return false;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type = AETHERNET_PACKET_TYPE_PROFILE_SYNC;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    bool delivered = service->sender->send(service->sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

bool aethernet_profile_handle_packet(aethernet_profile_service_t *service,
                                     const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_PROFILE_SYNC) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    // Decode the payload via the vendored cJSON, mirroring the C# HandleAsync which deserializes
    // ProfileSyncPayload. Malformed → benign drop (C# swallows JsonException and returns false).
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;

    const cJSON *juhid = cJSON_GetObjectItemCaseSensitive(body, "uhid");
    const cJSON *jname = cJSON_GetObjectItemCaseSensitive(body, "display_name");
    const cJSON *javat = cJSON_GetObjectItemCaseSensitive(body, "avatar_ref");
    const cJSON *jstat = cJSON_GetObjectItemCaseSensitive(body, "status_message");
    const cJSON *jupd  = cJSON_GetObjectItemCaseSensitive(body, "updated_at_ms");

    // uhid must be a present, non-empty string (mirrors C# string.IsNullOrEmpty check).
    if (!cJSON_IsString(juhid) || juhid->valuestring == NULL || juhid->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }
    const char *uhid = juhid->valuestring;

    // Ignore our own profile echoed back.
    if (service->sender->local_uhid && strcmp(uhid, service->sender->local_uhid) == 0) {
        cJSON_Delete(body);
        return false;
    }

    const char *display_name = (cJSON_IsString(jname) && jname->valuestring) ? jname->valuestring : "";
    const char *avatar_ref = (cJSON_IsString(javat) && javat->valuestring) ? javat->valuestring : "";
    const char *status_message = (cJSON_IsString(jstat) && jstat->valuestring) ? jstat->valuestring : "";
    int64_t updated_at_ms = cJSON_IsNumber(jupd) ? (int64_t)jupd->valuedouble : 0;

    // Upsert the peer's profile, keyed by uhid (mirrors _peerProfiles[body.Uhid] = body). Build the
    // replacement fields first so a mid-copy OOM leaves the existing cached entry intact.
    aethernet_profile_t next = {0};
    if (!profile_fields_set(&next, uhid, display_name, avatar_ref, status_message, updated_at_ms)) {
        cJSON_Delete(body);
        return false;
    }
    cJSON_Delete(body);

    profile_node_t *node = peer_find_prof(service, uhid);
    if (node == NULL) {
        node = (profile_node_t *)calloc(1, sizeof(profile_node_t));
        if (!node) { profile_fields_free(&next); return false; }
        node->next = service->peers;
        service->peers = node;
    } else {
        profile_fields_free(&node->profile);
    }
    node->profile = next;

    if (service->updated_cb) {
        service->updated_cb(&node->profile, service->updated_cb_user_data);
    }
    return true;
}

const aethernet_profile_t *aethernet_profile_get(const aethernet_profile_service_t *service,
                                                 const char *uhid) {
    if (!service || !uhid) return NULL;
    profile_node_t *node = peer_find_prof((aethernet_profile_service_t *)service, uhid);
    return node ? &node->profile : NULL;
}

int aethernet_profile_get_known(const aethernet_profile_service_t *service,
                                aethernet_profile_t **out_profiles,
                                int *out_count) {
    if (!service || !out_profiles || !out_count) return -1;

    int count = 0;
    for (profile_node_t *n = service->peers; n; n = n->next) count++;
    if (count == 0) {
        *out_profiles = NULL;
        *out_count = 0;
        return 0;
    }

    aethernet_profile_t *arr = (aethernet_profile_t *)calloc((size_t)count, sizeof(*arr));
    if (!arr) return -1;

    int i = 0;
    for (profile_node_t *n = service->peers; n && i < count; n = n->next) {
        if (!profile_fields_set(&arr[i], n->profile.uhid, n->profile.display_name,
                                n->profile.avatar_ref, n->profile.status_message,
                                n->profile.updated_at_ms)) {
            // OOM mid-copy — unwind everything allocated so far.
            for (int j = 0; j < i; j++) profile_fields_free(&arr[j]);
            free(arr);
            return -1;
        }
        i++;
    }
    *out_profiles = arr;
    *out_count = count;
    return count;
}

void aethernet_profile_list_free(aethernet_profile_t *profiles, int count) {
    if (!profiles) return;
    for (int i = 0; i < count; i++) profile_fields_free(&profiles[i]);
    free(profiles);
}

void aethernet_profile_set_updated_cb(aethernet_profile_service_t *service,
                                      aethernet_profile_updated_cb cb,
                                      void *user_data) {
    if (!service) return;
    service->updated_cb = cb;
    service->updated_cb_user_data = user_data;
}
