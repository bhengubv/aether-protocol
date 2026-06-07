// SPDX-License-Identifier: MIT
// Aether DirectoryService — application-layer name -> ContentDescriptor resolver.
//
// Mirrors AetherNet.Content.DirectoryService (C# reference). Wire payloads are
// JSON with snake_case field names, encoded via cJSON. Catalogue and pending-
// query tracking are linked-list-backed (single-threaded, embedded-friendly).
//
// NOTE: this implementation is single-threaded by design; hosts that pump
// packets from multiple threads wrap the service in their own mutex. The C
// reference resolve path is non-blocking — see directory_service.h for the
// caller contract.

#include "aethernet/directory_service.h"
#include "aethernet/constants.h"

#include <cjson/cJSON.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// ─── String helper ────────────────────────────────────────

static char *dir_str_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// ─── ContentDescriptor lifecycle ─────────────────────────

aethernet_content_descriptor_t *aethernet_content_descriptor_new(void) {
    aethernet_content_descriptor_t *d =
        (aethernet_content_descriptor_t *)calloc(1, sizeof(aethernet_content_descriptor_t));
    return d;
}

void aethernet_content_descriptor_free(aethernet_content_descriptor_t *desc) {
    if (!desc) return;
    free(desc->root_hash);
    free(desc->name);
    free(desc->content_type);
    free(desc->created_at);
    if (desc->chunk_hashes) {
        for (int32_t i = 0; i < desc->chunk_hashes_count; i++) {
            free(desc->chunk_hashes[i]);
        }
        free(desc->chunk_hashes);
    }
    free(desc);
}

aethernet_content_descriptor_t *aethernet_content_descriptor_clone(const aethernet_content_descriptor_t *src) {
    if (!src) return NULL;
    aethernet_content_descriptor_t *d = aethernet_content_descriptor_new();
    if (!d) return NULL;
    d->root_hash         = dir_str_dup(src->root_hash);
    d->name              = dir_str_dup(src->name);
    d->content_type      = dir_str_dup(src->content_type);
    d->created_at        = dir_str_dup(src->created_at);
    d->total_bytes       = src->total_bytes;
    d->chunk_size_bytes  = src->chunk_size_bytes;
    d->chunk_count       = src->chunk_count;
    if (src->chunk_hashes_count > 0 && src->chunk_hashes) {
        d->chunk_hashes = (char **)calloc((size_t)src->chunk_hashes_count, sizeof(char *));
        if (!d->chunk_hashes) {
            aethernet_content_descriptor_free(d);
            return NULL;
        }
        d->chunk_hashes_count = src->chunk_hashes_count;
        for (int32_t i = 0; i < src->chunk_hashes_count; i++) {
            d->chunk_hashes[i] = dir_str_dup(src->chunk_hashes[i]);
        }
    }
    return d;
}

// Internal: read fields from a cJSON object into a freshly-allocated descriptor.
static aethernet_content_descriptor_t *descriptor_from_json(const cJSON *obj) {
    if (!cJSON_IsObject(obj)) return NULL;
    aethernet_content_descriptor_t *d = aethernet_content_descriptor_new();
    if (!d) return NULL;

    const cJSON *jroot = cJSON_GetObjectItemCaseSensitive(obj, "root_hash");
    const cJSON *jname = cJSON_GetObjectItemCaseSensitive(obj, "name");
    const cJSON *jtb   = cJSON_GetObjectItemCaseSensitive(obj, "total_bytes");
    const cJSON *jcsb  = cJSON_GetObjectItemCaseSensitive(obj, "chunk_size_bytes");
    const cJSON *jcc   = cJSON_GetObjectItemCaseSensitive(obj, "chunk_count");
    const cJSON *jch   = cJSON_GetObjectItemCaseSensitive(obj, "chunk_hashes");
    const cJSON *jct   = cJSON_GetObjectItemCaseSensitive(obj, "content_type");
    const cJSON *jca   = cJSON_GetObjectItemCaseSensitive(obj, "created_at");

    if (cJSON_IsString(jroot) && jroot->valuestring) d->root_hash    = dir_str_dup(jroot->valuestring);
    if (cJSON_IsString(jname) && jname->valuestring) d->name         = dir_str_dup(jname->valuestring);
    if (cJSON_IsString(jct)   && jct->valuestring)   d->content_type = dir_str_dup(jct->valuestring);
    if (cJSON_IsString(jca)   && jca->valuestring)   d->created_at   = dir_str_dup(jca->valuestring);
    if (cJSON_IsNumber(jtb))  d->total_bytes      = (int64_t)jtb->valuedouble;
    if (cJSON_IsNumber(jcsb)) d->chunk_size_bytes = (int32_t)jcsb->valuedouble;
    if (cJSON_IsNumber(jcc))  d->chunk_count      = (int32_t)jcc->valuedouble;

    if (cJSON_IsArray(jch)) {
        int n = cJSON_GetArraySize(jch);
        if (n > 0) {
            d->chunk_hashes = (char **)calloc((size_t)n, sizeof(char *));
            if (d->chunk_hashes) {
                d->chunk_hashes_count = (int32_t)n;
                for (int i = 0; i < n; i++) {
                    const cJSON *item = cJSON_GetArrayItem(jch, i);
                    if (cJSON_IsString(item) && item->valuestring) {
                        d->chunk_hashes[i] = dir_str_dup(item->valuestring);
                    }
                }
            }
        }
    }
    return d;
}

// Internal: serialise a descriptor to a freshly-allocated cJSON object. Caller
// either attaches it to a parent (cJSON_AddItemToObject) or deletes it.
static cJSON *descriptor_to_json(const aethernet_content_descriptor_t *d) {
    if (!d) return NULL;
    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;
    cJSON_AddStringToObject(obj, "root_hash",        d->root_hash    ? d->root_hash    : "");
    cJSON_AddStringToObject(obj, "name",             d->name         ? d->name         : "");
    cJSON_AddNumberToObject(obj, "total_bytes",      (double)d->total_bytes);
    cJSON_AddNumberToObject(obj, "chunk_size_bytes", (double)d->chunk_size_bytes);
    cJSON_AddNumberToObject(obj, "chunk_count",      (double)d->chunk_count);
    cJSON_AddStringToObject(obj, "content_type",     d->content_type ? d->content_type : "");
    cJSON_AddStringToObject(obj, "created_at",       d->created_at   ? d->created_at   : "");

    cJSON *arr = cJSON_CreateArray();
    if (arr) {
        for (int32_t i = 0; i < d->chunk_hashes_count; i++) {
            const char *h = d->chunk_hashes ? d->chunk_hashes[i] : NULL;
            cJSON_AddItemToArray(arr, cJSON_CreateString(h ? h : ""));
        }
        cJSON_AddItemToObject(obj, "chunk_hashes", arr);
    }
    return obj;
}

// ─── Service internals ───────────────────────────────────

typedef struct catalogue_node {
    char *name;                              // owned
    aethernet_content_descriptor_t *descriptor; // owned
    struct catalogue_node *next;
} catalogue_node_t;

// A pending outbound query that's awaiting a NamePublish response. Tracked so
// `aethernet_directory_resolve` can match incoming responses to outstanding
// queries via the `in_response_to_query_id` correlation field on the wire.
typedef struct pending_query_node {
    char query_id[37];                       // canonical hyphenated UUID + NUL (e.g. "550e8400-e29b-41d4-a716-446655440000")
    char *name;                              // owned — the name being awaited
    bool completed;                          // set to true on response arrival
    struct pending_query_node *next;
} pending_query_node_t;

struct aethernet_directory_service {
    aethernet_mesh_sender_t *sender;
    catalogue_node_t *catalogue;
    pending_query_node_t *pending_queries;

    aethernet_directory_entry_announced_cb on_entry_announced;
    void *on_entry_announced_user_data;
};

static catalogue_node_t *find_or_insert_catalogue(aethernet_directory_service_t *svc, const char *name) {
    for (catalogue_node_t *n = svc->catalogue; n; n = n->next) {
        if (n->name && strcmp(n->name, name) == 0) return n;
    }
    catalogue_node_t *node = (catalogue_node_t *)calloc(1, sizeof(catalogue_node_t));
    if (!node) return NULL;
    node->name = dir_str_dup(name);
    if (!node->name) { free(node); return NULL; }
    node->next = svc->catalogue;
    svc->catalogue = node;
    return node;
}

static catalogue_node_t *find_catalogue(aethernet_directory_service_t *svc, const char *name) {
    for (catalogue_node_t *n = svc->catalogue; n; n = n->next) {
        if (n->name && strcmp(n->name, name) == 0) return n;
    }
    return NULL;
}

// Replace a catalogue node's descriptor with a deep copy of `src`. Frees the
// existing descriptor first. Returns 0 on success, -1 on OOM.
static int catalogue_store(aethernet_directory_service_t *svc, const char *name,
                           const aethernet_content_descriptor_t *src) {
    catalogue_node_t *node = find_or_insert_catalogue(svc, name);
    if (!node) return -1;
    aethernet_content_descriptor_t *copy = aethernet_content_descriptor_clone(src);
    if (!copy) return -1;
    if (node->descriptor) aethernet_content_descriptor_free(node->descriptor);
    node->descriptor = copy;
    return 0;
}

// ─── Wire-format helpers ─────────────────────────────────

// 16-byte random buffer rendered as canonical hyphenated UUID (RFC 4122 v4).
// Reuses the same digit-shape as the rest of the C codebase. We rely on rand()
// here for cross-platform reach; hosts that need cryptographic uniqueness wire
// in libsodium randombytes_buf on their side.
static void make_uuid_v4(char out[37]) {
    uint8_t b[16];
    for (int i = 0; i < 16; i++) b[i] = (uint8_t)(rand() & 0xFF);
    b[6] = (uint8_t)((b[6] & 0x0F) | 0x40); // version 4
    b[8] = (uint8_t)((b[8] & 0x3F) | 0x80); // variant 10x
    static const char hex[] = "0123456789abcdef";
    int o = 0;
    for (int i = 0; i < 16; i++) {
        out[o++] = hex[(b[i] >> 4) & 0xF];
        out[o++] = hex[b[i] & 0xF];
        if (i == 3 || i == 5 || i == 7 || i == 9) out[o++] = '-';
    }
    out[o] = '\0';
}

// Build a NamePublishPayload JSON body. Returns malloc'd UTF-8 bytes (caller
// frees) and writes the length to `*out_len`. `in_response_to_query_id` may be
// NULL for an unsolicited publish.
static uint8_t *build_name_publish_body(const char *name,
                                        const aethernet_content_descriptor_t *desc,
                                        const char *in_response_to_query_id,
                                        uint32_t *out_len) {
    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;
    cJSON_AddStringToObject(obj, "name", name);

    cJSON *jdesc = descriptor_to_json(desc);
    if (jdesc) cJSON_AddItemToObject(obj, "descriptor", jdesc);

    // Match the C# wire shape exactly: snake_case, nullable Guid serialised as
    // null when absent. Cross-language byte-equality assertion lives in the
    // shared fixtures corpus.
    if (in_response_to_query_id && in_response_to_query_id[0] != '\0') {
        cJSON_AddStringToObject(obj, "in_response_to_query_id", in_response_to_query_id);
    } else {
        cJSON_AddNullToObject(obj, "in_response_to_query_id");
    }

    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return NULL;
    *out_len = (uint32_t)strlen(body);
    return (uint8_t *)body;
}

// Build a NameQueryPayload JSON body. Returns malloc'd UTF-8 bytes (caller
// frees) and writes the length to `*out_len`.
static uint8_t *build_name_query_body(const char *name, const char *query_id, uint32_t *out_len) {
    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;
    cJSON_AddStringToObject(obj, "name", name);
    cJSON_AddStringToObject(obj, "query_id", query_id);
    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return NULL;
    *out_len = (uint32_t)strlen(body);
    return (uint8_t *)body;
}

// ─── Public API ──────────────────────────────────────────

int aethernet_directory_service_init(aethernet_directory_service_t **out_svc,
                                  aethernet_mesh_sender_t *sender) {
    if (!out_svc || !sender) return -1;
    aethernet_directory_service_t *svc =
        (aethernet_directory_service_t *)calloc(1, sizeof(aethernet_directory_service_t));
    if (!svc) return -1;
    svc->sender = sender;
    *out_svc = svc;
    return 0;
}

void aethernet_directory_service_free(aethernet_directory_service_t *svc) {
    if (!svc) return;
    while (svc->catalogue) {
        catalogue_node_t *next = svc->catalogue->next;
        free(svc->catalogue->name);
        if (svc->catalogue->descriptor) aethernet_content_descriptor_free(svc->catalogue->descriptor);
        free(svc->catalogue);
        svc->catalogue = next;
    }
    while (svc->pending_queries) {
        pending_query_node_t *next = svc->pending_queries->next;
        free(svc->pending_queries->name);
        free(svc->pending_queries);
        svc->pending_queries = next;
    }
    free(svc);
}

void aethernet_directory_set_entry_announced_callback(
    aethernet_directory_service_t *svc,
    aethernet_directory_entry_announced_cb cb,
    void *user_data
) {
    if (!svc) return;
    svc->on_entry_announced = cb;
    svc->on_entry_announced_user_data = user_data;
}

int aethernet_directory_list_names(aethernet_directory_service_t *svc,
                                const char **names_out, int max_names) {
    if (!svc) return -1;
    int n = 0;
    for (catalogue_node_t *node = svc->catalogue; node; node = node->next) {
        if (names_out && n < max_names) names_out[n] = node->name;
        n++;
    }
    return n;
}

int aethernet_directory_publish(aethernet_directory_service_t *svc,
                             const char *name,
                             const aethernet_content_descriptor_t *descriptor) {
    if (!svc || !name || !*name || !descriptor) return -1;
    if (catalogue_store(svc, name, descriptor) != 0) return -1;

    uint32_t body_len = 0;
    uint8_t *body = build_name_publish_body(name, descriptor, NULL, &body_len);
    if (!body) return -1;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_NAME_PUBLISH;
    aethernet_packet_set_source_uhid(pkt, svc->sender->local_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    if (svc->sender->broadcast) {
        svc->sender->broadcast(svc->sender, pkt);
    }
    aethernet_packet_free(pkt);
    return 0;
}

int aethernet_directory_resolve(aethernet_directory_service_t *svc,
                             const char *name,
                             aethernet_content_descriptor_t *out_descriptor,
                             int timeout_seconds) {
    (void)timeout_seconds;  // single-threaded impl does not block — see header doc
    if (!svc || !name || !*name || !out_descriptor) return -1;

    catalogue_node_t *hit = find_catalogue(svc, name);
    if (hit && hit->descriptor) {
        aethernet_content_descriptor_t *clone = aethernet_content_descriptor_clone(hit->descriptor);
        if (!clone) return -1;
        // Move the cloned descriptor's owned fields into the caller-supplied
        // struct. The caller free()s the strings via aethernet_content_descriptor_free
        // on the static struct? Cleaner: copy field-by-field, free the clone shell.
        *out_descriptor = *clone;
        free(clone);  // shallow — owned ptrs moved into out_descriptor
        return 0;
    }

    // No local hit — register a pending query, build a NameQuery packet, broadcast.
    char query_id[37];
    make_uuid_v4(query_id);

    pending_query_node_t *pending = (pending_query_node_t *)calloc(1, sizeof(pending_query_node_t));
    if (!pending) return -1;
    memcpy(pending->query_id, query_id, sizeof(query_id));
    pending->name = dir_str_dup(name);
    if (!pending->name) { free(pending); return -1; }
    pending->next = svc->pending_queries;
    svc->pending_queries = pending;

    uint32_t body_len = 0;
    uint8_t *body = build_name_query_body(name, query_id, &body_len);
    if (!body) return -1;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_NAME_QUERY;
    aethernet_packet_set_source_uhid(pkt, svc->sender->local_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    if (svc->sender->broadcast) {
        svc->sender->broadcast(svc->sender, pkt);
    }
    aethernet_packet_free(pkt);
    return 1;  // query broadcast — caller polls
}

// Internal: handle an inbound NamePublish. Stores in catalogue, signals any
// matching pending query, and fires the entry-announced callback.
static int handle_name_publish(aethernet_directory_service_t *svc,
                               const aethernet_mesh_packet_t *packet) {
    if (!packet->payload || packet->payload_len == 0) return -1;
    cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (!obj) return -1;

    const cJSON *jname = cJSON_GetObjectItemCaseSensitive(obj, "name");
    const cJSON *jdesc = cJSON_GetObjectItemCaseSensitive(obj, "descriptor");
    const cJSON *jirtq = cJSON_GetObjectItemCaseSensitive(obj, "in_response_to_query_id");

    const char *name = cJSON_IsString(jname) ? jname->valuestring : NULL;
    if (!name || !*name) { cJSON_Delete(obj); return -1; }
    if (!cJSON_IsObject(jdesc)) { cJSON_Delete(obj); return -1; }

    aethernet_content_descriptor_t *desc = descriptor_from_json(jdesc);
    if (!desc) { cJSON_Delete(obj); return -1; }

    // Store in catalogue (deep-copy via catalogue_store; we then free our own copy).
    int rc = catalogue_store(svc, name, desc);
    if (rc != 0) {
        aethernet_content_descriptor_free(desc);
        cJSON_Delete(obj);
        return -1;
    }

    // Query-response correlation: if this publish carries an
    // `in_response_to_query_id` matching one of our outstanding queries, mark
    // it complete so resolve-pollers see the catalogue hit.
    if (cJSON_IsString(jirtq) && jirtq->valuestring && jirtq->valuestring[0]) {
        for (pending_query_node_t *p = svc->pending_queries; p; p = p->next) {
            if (strcmp(p->query_id, jirtq->valuestring) == 0) {
                p->completed = true;
                break;
            }
        }
    }

    // Fire entry-announced callback (catalogue copy is the canonical one; we
    // surface our local desc which is a sibling deep copy — semantically equal).
    if (svc->on_entry_announced) {
        svc->on_entry_announced(
            svc->on_entry_announced_user_data,
            name,
            desc,
            packet->source_uhid);
    }

    aethernet_content_descriptor_free(desc);
    cJSON_Delete(obj);
    return 0;
}

// Internal: handle an inbound NameQuery. If we hold the requested name,
// unicast a NamePublish response with in_response_to_query_id = query_id.
// Otherwise silently ignore — other peers may answer.
static int handle_name_query(aethernet_directory_service_t *svc,
                             const aethernet_mesh_packet_t *packet) {
    if (!packet->payload || packet->payload_len == 0) return -1;
    cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (!obj) return -1;

    const cJSON *jname = cJSON_GetObjectItemCaseSensitive(obj, "name");
    const cJSON *jqid  = cJSON_GetObjectItemCaseSensitive(obj, "query_id");

    const char *name = cJSON_IsString(jname) ? jname->valuestring : NULL;
    const char *qid  = cJSON_IsString(jqid)  ? jqid->valuestring  : NULL;
    if (!name || !qid) { cJSON_Delete(obj); return -1; }

    catalogue_node_t *hit = find_catalogue(svc, name);
    if (!hit || !hit->descriptor) {
        cJSON_Delete(obj);
        return 0;  // unknown — silent ignore by spec
    }

    uint32_t body_len = 0;
    uint8_t *body = build_name_publish_body(name, hit->descriptor, qid, &body_len);
    if (!body) { cJSON_Delete(obj); return -1; }

    aethernet_mesh_packet_t *resp = aethernet_packet_new();
    if (!resp) { free(body); cJSON_Delete(obj); return -1; }
    resp->type = AETHERNET_PACKET_TYPE_NAME_PUBLISH;
    aethernet_packet_set_source_uhid(resp, svc->sender->local_uhid);
    if (packet->source_uhid) aethernet_packet_set_destination_uhid(resp, packet->source_uhid);
    resp->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(resp, body, body_len);
    free(body);

    if (svc->sender->send && packet->source_uhid) {
        svc->sender->send(svc->sender, resp, packet->source_uhid);
    }
    aethernet_packet_free(resp);
    cJSON_Delete(obj);
    return 0;
}

int aethernet_directory_handle(aethernet_directory_service_t *svc,
                            const aethernet_mesh_packet_t *packet) {
    if (!svc || !packet) return -1;
    if (packet->type == AETHERNET_PACKET_TYPE_NAME_PUBLISH) {
        return handle_name_publish(svc, packet);
    } else if (packet->type == AETHERNET_PACKET_TYPE_NAME_QUERY) {
        return handle_name_query(svc, packet);
    }
    return 0;  // non-directory packet: silently ignored
}
