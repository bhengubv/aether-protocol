// SPDX-License-Identifier: MIT
// Aether Mesh — Streaming, Video Call, and Watch-Together services.
//
// Single-threaded reference implementation. Hosts pumping packets from multiple
// threads must serialise calls behind their own mutex.
//
// Subscriber list per stream: fixed-size array, max AETHERNET_MAX_STREAM_SUBSCRIBERS (64).
// Exceeding this limit silently drops new subscribers — document in your host layer.
//
// JSON via cJSON (wired in CMakeLists.txt via FetchContent).
//
// NOTE: Build verification requires Linux/macOS with cmake + libsodium.
// CI on ubuntu-latest is the verification gate.

#include "aethernet/streaming.h"
#include "aethernet/voice.h"     // for aethernet_voice_call_state_t values
#include "aethernet/constants.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#include <cjson/cJSON.h>

// ─── LE helpers (file-private) ────────────────────────────

static void st_write_le_u32(uint8_t *buf, uint32_t v) {
    buf[0] = (uint8_t)(v);
    buf[1] = (uint8_t)(v >> 8);
    buf[2] = (uint8_t)(v >> 16);
    buf[3] = (uint8_t)(v >> 24);
}

static void st_write_le_i64(uint8_t *buf, int64_t v) {
    uint64_t u = (uint64_t)v;
    buf[0] = (uint8_t)(u);
    buf[1] = (uint8_t)(u >> 8);
    buf[2] = (uint8_t)(u >> 16);
    buf[3] = (uint8_t)(u >> 24);
    buf[4] = (uint8_t)(u >> 32);
    buf[5] = (uint8_t)(u >> 40);
    buf[6] = (uint8_t)(u >> 48);
    buf[7] = (uint8_t)(u >> 56);
}

static uint32_t st_read_le_u32(const uint8_t *buf) {
    return (uint32_t)buf[0]
         | ((uint32_t)buf[1] << 8)
         | ((uint32_t)buf[2] << 16)
         | ((uint32_t)buf[3] << 24);
}

static int64_t st_read_le_i64(const uint8_t *buf) {
    uint64_t u = (uint64_t)buf[0]
               | ((uint64_t)buf[1] << 8)
               | ((uint64_t)buf[2] << 16)
               | ((uint64_t)buf[3] << 24)
               | ((uint64_t)buf[4] << 32)
               | ((uint64_t)buf[5] << 40)
               | ((uint64_t)buf[6] << 48)
               | ((uint64_t)buf[7] << 56);
    return (int64_t)u;
}

// ─── Shared helpers ───────────────────────────────────────

static int64_t st_now_ms(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *st_str_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void st_uuid_to_canonical(const uint8_t id[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0],id[1],id[2],id[3],id[4],id[5],id[6],id[7],
        id[8],id[9],id[10],id[11],id[12],id[13],id[14],id[15]);
}

static bool st_parse_uuid(const char *s, uint8_t out[16]) {
    if (!s) return false;
    size_t len = strlen(s);
    if (len == 36) {
        char compact[33];
        int ci = 0;
        for (int i = 0; i < 36 && ci < 32; i++) {
            if (s[i] == '-') continue;
            compact[ci++] = s[i];
        }
        compact[32] = 0;
        s = compact; len = 32;
    }
    if (len != 32) return false;
    for (int i = 0; i < 16; i++) {
        unsigned int byte;
        char tmp[3] = { s[i*2], s[i*2+1], 0 };
        if (sscanf(tmp, "%02x", &byte) != 1) return false;
        out[i] = (uint8_t)byte;
    }
    return true;
}

static void st_gen_uuid_v4(uint8_t out[16]) {
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(st_now_ms() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < 16; i++) out[i] = (uint8_t)(rand() & 0xFF);
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// ─── Binary frame helpers ─────────────────────────────────

// StreamSegment / VideoFrame: [16 Id BE][4 Seq LE][8 TsMs LE][1 flag][N data]

static uint8_t *build_av_frame(
    const uint8_t *id16,
    uint32_t seq, int64_t ts_ms, uint8_t flag,
    const uint8_t *data, size_t data_len,
    uint32_t *out_len
) {
    size_t total = 16 + 4 + 8 + 1 + data_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;
    memcpy(buf, id16, 16);
    st_write_le_u32(buf + 16, seq);
    st_write_le_i64(buf + 20, ts_ms);
    buf[28] = flag;
    if (data && data_len) memcpy(buf + 29, data, data_len);
    *out_len = (uint32_t)total;
    return buf;
}

static bool parse_av_frame(
    const uint8_t *raw, size_t raw_len,
    uint8_t id16_out[16], uint32_t *seq_out, int64_t *ts_out,
    uint8_t *flag_out, const uint8_t **payload_out, size_t *payload_len_out
) {
    if (raw_len < 29) return false;
    memcpy(id16_out, raw, 16);
    *seq_out     = st_read_le_u32(raw + 16);
    *ts_out      = st_read_le_i64(raw + 20);
    *flag_out    = raw[28];
    *payload_out = raw + 29;
    *payload_len_out = raw_len - 29;
    return true;
}

// ─── Send helpers ─────────────────────────────────────────

/* Serialise the packet and dispatch the wire bytes to the routed next hop via the
   bound transport. Returns 1 if transmitted, 0 when no transport is bound or no route
   is known. The real serialise + send the C# StreamingService does via
   IMeshSender.SendAsync. */
static int st_route_and_send(aethernet_transport_t *transport,
                             aethernet_routing_service_t *routing,
                             const aethernet_mesh_packet_t *pkt, const char *to_uhid) {
    if (!transport || !transport->vtable || !transport->vtable->send || !pkt || !to_uhid) return 0;
    aethernet_route_entry_t *route = NULL;
    if (!aethernet_routing_find_cached(routing, to_uhid, &route)) return 0;
    int sent = 0;
    size_t cap = aethernet_packet_estimate_size(pkt) + 64;
    uint8_t *buf = (uint8_t *)malloc(cap);
    if (buf) {
        int n = aethernet_packet_serialize(pkt, buf, cap);
        if (n > 0) {
            transport->vtable->send(transport->handle, route->next_hop_uhid, buf, (size_t)n);
            sent = 1;
        }
        free(buf);
    }
    aethernet_route_entry_free(route);
    return sent;
}

static void st_send_json_unicast(
    aethernet_transport_t *transport,
    aethernet_routing_service_t *routing,
    const char *local_uhid,
    cJSON *obj,
    const char *to_uhid,
    uint8_t pkt_type,
    uint8_t priority
) {
    if (!obj || !to_uhid) { cJSON_Delete(obj); return; }
    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return; }
    pkt->type = pkt_type;
    aethernet_packet_set_source_uhid(pkt, local_uhid);
    aethernet_packet_set_destination_uhid(pkt, to_uhid);
    pkt->ttl      = AETHERNET_DEFAULT_TTL;
    pkt->priority = priority;
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    free(body);

    /* Serialise the packet and transmit it to the routed next hop. */
    st_route_and_send(transport, routing, pkt, to_uhid);
    aethernet_packet_free(pkt);
}

static void st_broadcast_json(
    aethernet_transport_t *transport,
    aethernet_routing_service_t *routing,
    const char *local_uhid,
    cJSON *obj,
    uint8_t pkt_type,
    uint8_t priority
) {
    if (!obj) return;
    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return; }
    pkt->type = pkt_type;
    aethernet_packet_set_source_uhid(pkt, local_uhid);
    pkt->ttl      = AETHERNET_DEFAULT_TTL;
    pkt->priority = priority;
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    free(body);

    /* Mesh-wide STREAM_ANNOUNCE flood: the low-level transport vtable exposes only a
       unicast send (no broadcast op) and this service holds no mesh sender, so the
       discovery announce cannot fan out from here yet. Tracked sub-gap: add a transport
       broadcast op or wire an aethernet_mesh_sender (sender->broadcast) into streaming. */
    (void)transport; (void)routing;
    aethernet_packet_free(pkt);
}

// ═══════════════════════════════════════════════════════════
// StreamingService
// ═══════════════════════════════════════════════════════════

#define ST_MAX_STREAMS 16

typedef struct {
    uint8_t  stream_id[16];
    char    *publisher_uhid;   // owned
    char    *title;            // owned
    char    *mime_type;        // owned
    char    *subscribers[AETHERNET_MAX_STREAM_SUBSCRIBERS];  // owned
    int      subscriber_count;
    uint32_t next_seq;
    bool     active;
    bool     subscribed;       // true if this node is a subscriber (not publisher)
} stream_record_t;

struct aethernet_streaming_service {
    aethernet_transport_t       *transport;
    aethernet_routing_service_t *routing;
    char                     *local_uhid;

    stream_record_t streams[ST_MAX_STREAMS];

    aethernet_stream_announced_cb announced_cb;
    void                      *announced_cb_ud;
    aethernet_stream_segment_cb   segment_cb;
    void                      *segment_cb_ud;
    aethernet_stream_ended_cb     ended_cb;
    void                      *ended_cb_ud;
};

static stream_record_t *st_find_stream(aethernet_streaming_service_t *svc, const uint8_t id[16]) {
    for (int i = 0; i < ST_MAX_STREAMS; i++)
        if (svc->streams[i].active && memcmp(svc->streams[i].stream_id, id, 16) == 0)
            return &svc->streams[i];
    return NULL;
}

static stream_record_t *st_alloc_stream(aethernet_streaming_service_t *svc) {
    for (int i = 0; i < ST_MAX_STREAMS; i++)
        if (!svc->streams[i].active) return &svc->streams[i];
    return NULL;
}

static void st_free_stream(stream_record_t *s) {
    if (!s) return;
    free(s->publisher_uhid); s->publisher_uhid = NULL;
    free(s->title);          s->title = NULL;
    free(s->mime_type);      s->mime_type = NULL;
    for (int i = 0; i < s->subscriber_count; i++) { free(s->subscribers[i]); s->subscribers[i] = NULL; }
    s->subscriber_count = 0;
    s->active = false;
    s->subscribed = false;
}

aethernet_streaming_service_t *aethernet_streaming_service_create(
    aethernet_transport_t *transport, aethernet_routing_service_t *routing, const char *local_uhid
) {
    if (!transport || !routing || !local_uhid) return NULL;
    aethernet_streaming_service_t *svc = (aethernet_streaming_service_t *)calloc(1, sizeof(aethernet_streaming_service_t));
    if (!svc) return NULL;
    svc->transport  = transport;
    svc->routing    = routing;
    svc->local_uhid = st_str_dup(local_uhid);
    if (!svc->local_uhid) { free(svc); return NULL; }
    return svc;
}

void aethernet_streaming_service_destroy(aethernet_streaming_service_t *svc) {
    if (!svc) return;
    for (int i = 0; i < ST_MAX_STREAMS; i++) st_free_stream(&svc->streams[i]);
    free(svc->local_uhid);
    free(svc);
}

void aethernet_streaming_set_announced_cb(aethernet_streaming_service_t *svc, aethernet_stream_announced_cb cb, void *ud) {
    if (svc) { svc->announced_cb = cb; svc->announced_cb_ud = ud; }
}
void aethernet_streaming_set_segment_cb(aethernet_streaming_service_t *svc, aethernet_stream_segment_cb cb, void *ud) {
    if (svc) { svc->segment_cb = cb; svc->segment_cb_ud = ud; }
}
void aethernet_streaming_set_ended_cb(aethernet_streaming_service_t *svc, aethernet_stream_ended_cb cb, void *ud) {
    if (svc) { svc->ended_cb = cb; svc->ended_cb_ud = ud; }
}

int aethernet_streaming_start(
    aethernet_streaming_service_t *svc, const char *title, const char *mime_type, uint8_t stream_id_out[16]
) {
    if (!svc || !title) return -1;
    stream_record_t *rec = st_alloc_stream(svc);
    if (!rec) return -1;

    st_gen_uuid_v4(rec->stream_id);
    memcpy(stream_id_out, rec->stream_id, 16);
    rec->publisher_uhid  = st_str_dup(svc->local_uhid);
    rec->title           = st_str_dup(title);
    rec->mime_type       = st_str_dup(mime_type ? mime_type : "application/octet-stream");
    rec->next_seq        = 0;
    rec->active          = true;
    rec->subscribed      = false;

    char id_str[37];
    st_uuid_to_canonical(rec->stream_id, id_str);

    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "stream_id", id_str);
    cJSON_AddStringToObject(obj, "publisher_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "title", title);
    cJSON_AddStringToObject(obj, "mime_type", rec->mime_type);
    cJSON_AddStringToObject(obj, "signal_type", "announce");
    st_broadcast_json(svc->transport, svc->routing, svc->local_uhid, obj,
                      AETHERNET_PACKET_TYPE_STREAM_ANNOUNCE, 32);
    return 0;
}

int aethernet_streaming_end(aethernet_streaming_service_t *svc, const uint8_t stream_id[16]) {
    if (!svc) return -1;
    stream_record_t *rec = st_find_stream(svc, stream_id);
    if (!rec) return -1;

    char id_str[37];
    st_uuid_to_canonical(rec->stream_id, id_str);

    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "stream_id", id_str);
    cJSON_AddStringToObject(obj, "publisher_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "end");

    for (int i = 0; i < rec->subscriber_count; i++) {
        if (rec->subscribers[i]) {
            cJSON *dup = cJSON_Duplicate(obj, 1);
            st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, dup,
                                 rec->subscribers[i], AETHERNET_PACKET_TYPE_STREAM_UNSUBSCRIBE, 32);
        }
    }
    cJSON_Delete(obj);

    if (svc->ended_cb) svc->ended_cb(stream_id, svc->ended_cb_ud);
    st_free_stream(rec);
    return 0;
}

int aethernet_streaming_publish_segment(
    aethernet_streaming_service_t *svc,
    const uint8_t *stream_id, const uint8_t *data, size_t data_len, int is_keyframe
) {
    if (!svc || !stream_id) return -1;
    stream_record_t *rec = st_find_stream(svc, stream_id);
    if (!rec) return -1;

    uint32_t seq   = rec->next_seq++;
    int64_t  ts_ms = st_now_ms();
    uint32_t frame_len = 0;
    uint8_t *frame = build_av_frame(stream_id, seq, ts_ms, (uint8_t)(is_keyframe ? 1 : 0),
                                    data, data_len, &frame_len);
    if (!frame) return -1;

    for (int i = 0; i < rec->subscriber_count; i++) {
        const char *sub = rec->subscribers[i];
        if (!sub) continue;
        aethernet_mesh_packet_t *pkt = aethernet_packet_new();
        if (!pkt) continue;
        pkt->type = AETHERNET_PACKET_TYPE_STREAM_SEGMENT;
        aethernet_packet_set_source_uhid(pkt, svc->local_uhid);
        aethernet_packet_set_destination_uhid(pkt, sub);
        pkt->ttl      = AETHERNET_DEFAULT_TTL;
        pkt->priority = 16;
        aethernet_packet_set_payload(pkt, frame, frame_len);
        /* Serialise the segment and transmit it to this subscriber's next hop. */
        st_route_and_send(svc->transport, svc->routing, pkt, sub);
        aethernet_packet_free(pkt);
    }
    free(frame);
    return 0;
}

int aethernet_streaming_subscribe(
    aethernet_streaming_service_t *svc, const uint8_t *stream_id, const char *publisher_uhid
) {
    if (!svc || !stream_id || !publisher_uhid) return -1;

    stream_record_t *rec = st_find_stream(svc, stream_id);
    if (!rec) {
        rec = st_alloc_stream(svc);
        if (!rec) return -1;
        memcpy(rec->stream_id, stream_id, 16);
        rec->publisher_uhid = st_str_dup(publisher_uhid);
        rec->active = true;
    }
    rec->subscribed = true;

    char id_str[37];
    st_uuid_to_canonical(stream_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "stream_id", id_str);
    cJSON_AddStringToObject(obj, "subscriber_uhid", svc->local_uhid);
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj,
                         publisher_uhid, AETHERNET_PACKET_TYPE_STREAM_SUBSCRIBE, 32);
    return 0;
}

int aethernet_streaming_unsubscribe(
    aethernet_streaming_service_t *svc, const uint8_t *stream_id, const char *publisher_uhid
) {
    if (!svc || !stream_id || !publisher_uhid) return -1;
    stream_record_t *rec = st_find_stream(svc, stream_id);
    if (rec) rec->subscribed = false;

    char id_str[37];
    st_uuid_to_canonical(stream_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "stream_id", id_str);
    cJSON_AddStringToObject(obj, "subscriber_uhid", svc->local_uhid);
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj,
                         publisher_uhid, AETHERNET_PACKET_TYPE_STREAM_UNSUBSCRIBE, 32);
    return 0;
}

int aethernet_streaming_handle_packet(aethernet_streaming_service_t *svc, const aethernet_packet_t *packet) {
    if (!svc || !packet) return -1;

    if (packet->type == AETHERNET_PACKET_TYPE_STREAM_SEGMENT) {
        if (!packet->payload || packet->payload_len < 29) return -1;
        uint8_t id[16]; uint32_t seq; int64_t ts; uint8_t flag;
        const uint8_t *payload; size_t payload_len;
        if (!parse_av_frame(packet->payload, packet->payload_len, id, &seq, &ts, &flag, &payload, &payload_len))
            return -1;
        stream_record_t *rec = st_find_stream(svc, id);
        if (rec && rec->subscribed && svc->segment_cb)
            svc->segment_cb(id, payload, payload_len, flag ? 1 : 0, ts, seq, svc->segment_cb_ud);
        return 0;
    }

    if (packet->type == AETHERNET_PACKET_TYPE_STREAM_ANNOUNCE ||
        packet->type == AETHERNET_PACKET_TYPE_STREAM_UNSUBSCRIBE) {
        if (!packet->payload || packet->payload_len == 0) return -1;
        cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
        if (!obj) return -1;

        cJSON *jid  = cJSON_GetObjectItemCaseSensitive(obj, "stream_id");
        cJSON *jsig = cJSON_GetObjectItemCaseSensitive(obj, "signal_type");
        const char *id_str = cJSON_IsString(jid) ? jid->valuestring : NULL;
        const char *sig    = cJSON_IsString(jsig) ? jsig->valuestring : NULL;

        uint8_t stream_id[16] = {0};
        if (!id_str || !st_parse_uuid(id_str, stream_id)) { cJSON_Delete(obj); return -1; }

        if (sig && strcmp(sig, "end") == 0) {
            stream_record_t *rec = st_find_stream(svc, stream_id);
            if (rec) {
                if (svc->ended_cb) svc->ended_cb(stream_id, svc->ended_cb_ud);
                st_free_stream(rec);
            }
        } else if (sig && strcmp(sig, "announce") == 0) {
            cJSON *jpub   = cJSON_GetObjectItemCaseSensitive(obj, "publisher_uhid");
            cJSON *jtitle = cJSON_GetObjectItemCaseSensitive(obj, "title");
            const char *pub   = cJSON_IsString(jpub)   ? jpub->valuestring   : packet->source_uhid;
            const char *title = cJSON_IsString(jtitle) ? jtitle->valuestring : "";
            stream_record_t *rec = st_find_stream(svc, stream_id);
            if (!rec) {
                rec = st_alloc_stream(svc);
                if (rec) {
                    memcpy(rec->stream_id, stream_id, 16);
                    rec->publisher_uhid = st_str_dup(pub);
                    rec->title          = st_str_dup(title);
                    rec->active         = true;
                }
            }
            if (svc->announced_cb) svc->announced_cb(stream_id, pub, title, svc->announced_cb_ud);
        }

        cJSON_Delete(obj);
        return 0;
    }

    if (packet->type == AETHERNET_PACKET_TYPE_STREAM_SUBSCRIBE) {
        if (!packet->payload || packet->payload_len == 0) return -1;
        cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
        if (!obj) return -1;
        cJSON *jid  = cJSON_GetObjectItemCaseSensitive(obj, "stream_id");
        cJSON *jsub = cJSON_GetObjectItemCaseSensitive(obj, "subscriber_uhid");
        const char *id_str = cJSON_IsString(jid) ? jid->valuestring : NULL;
        const char *sub    = cJSON_IsString(jsub) ? jsub->valuestring : packet->source_uhid;
        uint8_t stream_id[16] = {0};
        if (id_str && st_parse_uuid(id_str, stream_id) && sub) {
            stream_record_t *rec = st_find_stream(svc, stream_id);
            if (rec && rec->subscriber_count < AETHERNET_MAX_STREAM_SUBSCRIBERS) {
                bool exists = false;
                for (int i = 0; i < rec->subscriber_count; i++)
                    if (rec->subscribers[i] && strcmp(rec->subscribers[i], sub) == 0) { exists = true; break; }
                if (!exists) rec->subscribers[rec->subscriber_count++] = st_str_dup(sub);
            }
        }
        cJSON_Delete(obj);
        return 0;
    }

    return -1;
}

// ═══════════════════════════════════════════════════════════
// VideoCallService
// ═══════════════════════════════════════════════════════════

#define VC_MAX_CALLS 32

typedef struct {
    uint8_t  call_id[16];
    char    *remote_uhid;   // owned
    int      state;         // aethernet_voice_call_state_t
    uint32_t next_seq;
    bool     active;
} video_call_record_t;

struct aethernet_video_call_service {
    aethernet_transport_t       *transport;
    aethernet_routing_service_t *routing;
    char                     *local_uhid;

    video_call_record_t calls[VC_MAX_CALLS];

    aethernet_video_incoming_cb        incoming_cb;
    void                           *incoming_cb_ud;
    aethernet_video_state_changed_cb   state_cb;
    void                           *state_cb_ud;
    aethernet_video_frame_cb           frame_cb;
    void                           *frame_cb_ud;
    aethernet_video_keyframe_request_cb kfr_cb;
    void                            *kfr_cb_ud;
    aethernet_video_quality_changed_cb  quality_cb;
    void                            *quality_cb_ud;
};

static video_call_record_t *vc_find(aethernet_video_call_service_t *svc, const uint8_t id[16]) {
    for (int i = 0; i < VC_MAX_CALLS; i++)
        if (svc->calls[i].active && memcmp(svc->calls[i].call_id, id, 16) == 0) return &svc->calls[i];
    return NULL;
}

static video_call_record_t *vc_alloc(aethernet_video_call_service_t *svc) {
    for (int i = 0; i < VC_MAX_CALLS; i++)
        if (!svc->calls[i].active) return &svc->calls[i];
    return NULL;
}

static void vc_free(video_call_record_t *c) {
    if (!c) return;
    free(c->remote_uhid); c->remote_uhid = NULL; c->active = false;
}

aethernet_video_call_service_t *aethernet_video_call_service_create(
    aethernet_transport_t *transport, aethernet_routing_service_t *routing, const char *local_uhid
) {
    if (!transport || !routing || !local_uhid) return NULL;
    aethernet_video_call_service_t *svc = (aethernet_video_call_service_t *)calloc(1, sizeof(aethernet_video_call_service_t));
    if (!svc) return NULL;
    svc->transport  = transport;
    svc->routing    = routing;
    svc->local_uhid = st_str_dup(local_uhid);
    if (!svc->local_uhid) { free(svc); return NULL; }
    return svc;
}

void aethernet_video_call_service_destroy(aethernet_video_call_service_t *svc) {
    if (!svc) return;
    for (int i = 0; i < VC_MAX_CALLS; i++) vc_free(&svc->calls[i]);
    free(svc->local_uhid);
    free(svc);
}

void aethernet_video_set_incoming_cb(aethernet_video_call_service_t *svc, aethernet_video_incoming_cb cb, void *ud) {
    if (svc) { svc->incoming_cb = cb; svc->incoming_cb_ud = ud; }
}
void aethernet_video_set_state_changed_cb(aethernet_video_call_service_t *svc, aethernet_video_state_changed_cb cb, void *ud) {
    if (svc) { svc->state_cb = cb; svc->state_cb_ud = ud; }
}
void aethernet_video_set_frame_cb(aethernet_video_call_service_t *svc, aethernet_video_frame_cb cb, void *ud) {
    if (svc) { svc->frame_cb = cb; svc->frame_cb_ud = ud; }
}
void aethernet_video_set_keyframe_request_cb(aethernet_video_call_service_t *svc, aethernet_video_keyframe_request_cb cb, void *ud) {
    if (svc) { svc->kfr_cb = cb; svc->kfr_cb_ud = ud; }
}
void aethernet_video_set_quality_changed_cb(aethernet_video_call_service_t *svc, aethernet_video_quality_changed_cb cb, void *ud) {
    if (svc) { svc->quality_cb = cb; svc->quality_cb_ud = ud; }
}

int aethernet_video_send_offer(
    aethernet_video_call_service_t *svc,
    const char *to_uhid,
    const char **video_codecs, int vc_count,
    const char **audio_codecs, int ac_count,
    uint8_t call_id_out[16]
) {
    if (!svc || !to_uhid) return -1;
    video_call_record_t *rec = vc_alloc(svc);
    if (!rec) return -1;

    st_gen_uuid_v4(rec->call_id);
    rec->remote_uhid = st_str_dup(to_uhid);
    if (!rec->remote_uhid) return -1;
    rec->state    = AETHERNET_VOICE_STATE_OUTGOING;
    rec->next_seq = 0;
    rec->active   = true;
    memcpy(call_id_out, rec->call_id, 16);

    char id_str[37];
    st_uuid_to_canonical(rec->call_id, id_str);

    cJSON *obj  = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "video_offer");
    cJSON *va = cJSON_CreateArray();
    for (int i = 0; i < vc_count; i++) if (video_codecs[i]) cJSON_AddItemToArray(va, cJSON_CreateString(video_codecs[i]));
    cJSON_AddItemToObject(obj, "video_codecs", va);
    cJSON *aa = cJSON_CreateArray();
    for (int i = 0; i < ac_count; i++) if (audio_codecs[i]) cJSON_AddItemToArray(aa, cJSON_CreateString(audio_codecs[i]));
    cJSON_AddItemToObject(obj, "audio_codecs", aa);
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj, to_uhid, AETHERNET_PACKET_TYPE_VIDEO_SIGNALING, 32);
    return 0;
}

int aethernet_video_accept_call(aethernet_video_call_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    video_call_record_t *rec = vc_find(svc, call_id);
    if (!rec || rec->state != AETHERNET_VOICE_STATE_INCOMING) return -1;
    rec->state = AETHERNET_VOICE_STATE_CONNECTED; rec->next_seq = 0;
    char id_str[37]; st_uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "video_accept");
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj, rec->remote_uhid, AETHERNET_PACKET_TYPE_VIDEO_SIGNALING, 32);
    if (svc->state_cb) svc->state_cb(call_id, AETHERNET_VOICE_STATE_CONNECTED, svc->state_cb_ud);
    return 0;
}

int aethernet_video_hang_up(aethernet_video_call_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    video_call_record_t *rec = vc_find(svc, call_id);
    if (!rec) return -1;
    char id_str[37]; st_uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "video_hangup");
    const char *remote = rec->remote_uhid;
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj, remote, AETHERNET_PACKET_TYPE_VIDEO_SIGNALING, 32);
    if (svc->state_cb) svc->state_cb(call_id, AETHERNET_VOICE_STATE_ENDED, svc->state_cb_ud);
    vc_free(rec);
    return 0;
}

int aethernet_video_send_frame(
    aethernet_video_call_service_t *svc, const uint8_t *call_id, const uint8_t *video, size_t video_len, int is_keyframe
) {
    if (!svc || !call_id) return -1;
    video_call_record_t *rec = vc_find(svc, call_id);
    if (!rec || rec->state != AETHERNET_VOICE_STATE_CONNECTED) return -1;
    uint32_t seq = rec->next_seq++;
    int64_t ts_ms = st_now_ms();
    uint32_t frame_len = 0;
    uint8_t *frame = build_av_frame(call_id, seq, ts_ms, (uint8_t)(is_keyframe ? 1 : 0), video, video_len, &frame_len);
    if (!frame) return -1;
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(frame); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_VIDEO_FRAME;
    aethernet_packet_set_source_uhid(pkt, svc->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, rec->remote_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL; pkt->priority = 64;
    aethernet_packet_set_payload(pkt, frame, frame_len);
    free(frame);
    /* Serialise the video frame and transmit it to the routed next hop. */
    st_route_and_send(svc->transport, svc->routing, pkt, rec->remote_uhid);
    aethernet_packet_free(pkt);
    return 0;
}

int aethernet_video_request_keyframe(aethernet_video_call_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    video_call_record_t *rec = vc_find(svc, call_id);
    if (!rec) return -1;
    char id_str[37]; st_uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "keyframe_request");
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj, rec->remote_uhid, AETHERNET_PACKET_TYPE_VIDEO_SIGNALING, 32);
    return 0;
}

int aethernet_video_notify_quality_change(aethernet_video_call_service_t *svc, const uint8_t call_id[16], const char *quality) {
    if (!svc || !quality) return -1;
    video_call_record_t *rec = vc_find(svc, call_id);
    if (!rec) return -1;
    char id_str[37]; st_uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "quality", quality);
    cJSON_AddStringToObject(obj, "signal_type", "quality_change");
    st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, obj, rec->remote_uhid, AETHERNET_PACKET_TYPE_VIDEO_SIGNALING, 32);
    return 0;
}

int aethernet_video_handle_packet(aethernet_video_call_service_t *svc, const aethernet_packet_t *packet) {
    if (!svc || !packet) return -1;

    if (packet->type == AETHERNET_PACKET_TYPE_VIDEO_FRAME) {
        if (!packet->payload || packet->payload_len < 29) return -1;
        uint8_t id[16]; uint32_t seq; int64_t ts; uint8_t flag;
        const uint8_t *vid; size_t vid_len;
        if (!parse_av_frame(packet->payload, packet->payload_len, id, &seq, &ts, &flag, &vid, &vid_len)) return -1;
        if (svc->frame_cb) svc->frame_cb(id, vid, vid_len, flag ? 1 : 0, ts, svc->frame_cb_ud);
        return 0;
    }

    if (packet->type == AETHERNET_PACKET_TYPE_VIDEO_SIGNALING) {
        if (!packet->payload || packet->payload_len == 0) return -1;
        cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
        if (!obj) return -1;

        cJSON *jid  = cJSON_GetObjectItemCaseSensitive(obj, "call_id");
        cJSON *jsig = cJSON_GetObjectItemCaseSensitive(obj, "signal_type");
        const char *id_str = cJSON_IsString(jid)  ? jid->valuestring  : NULL;
        const char *sig    = cJSON_IsString(jsig) ? jsig->valuestring : NULL;

        uint8_t call_id[16] = {0};
        if (!id_str || !st_parse_uuid(id_str, call_id)) { cJSON_Delete(obj); return -1; }

        if (!sig) {
            /* Offer */
            video_call_record_t *rec = vc_alloc(svc);
            if (rec) {
                memcpy(rec->call_id, call_id, 16);
                rec->remote_uhid = st_str_dup(packet->source_uhid);
                rec->state = AETHERNET_VOICE_STATE_INCOMING; rec->active = true;
                if (svc->incoming_cb) {
                    cJSON *jva = cJSON_GetObjectItemCaseSensitive(obj, "video_codecs");
                    cJSON *jaa = cJSON_GetObjectItemCaseSensitive(obj, "audio_codecs");
                    int vc = cJSON_IsArray(jva) ? cJSON_GetArraySize(jva) : 0;
                    int ac = cJSON_IsArray(jaa) ? cJSON_GetArraySize(jaa) : 0;
                    const char **vcodecs = vc > 0 ? (const char **)malloc(sizeof(char *)*(size_t)vc) : NULL;
                    const char **acodecs = ac > 0 ? (const char **)malloc(sizeof(char *)*(size_t)ac) : NULL;
                    for (int i = 0; i < vc; i++) { cJSON *ci = cJSON_GetArrayItem(jva, i); if (vcodecs) vcodecs[i] = cJSON_IsString(ci) ? ci->valuestring : ""; }
                    for (int i = 0; i < ac; i++) { cJSON *ci = cJSON_GetArrayItem(jaa, i); if (acodecs) acodecs[i] = cJSON_IsString(ci) ? ci->valuestring : ""; }
                    svc->incoming_cb(call_id, packet->source_uhid, vcodecs, vc, acodecs, ac, svc->incoming_cb_ud);
                    free(vcodecs); free(acodecs);
                }
            }
        } else if (strcmp(sig, "video_accept") == 0) {
            video_call_record_t *rec = vc_find(svc, call_id);
            if (rec) { rec->state = AETHERNET_VOICE_STATE_CONNECTED; if (svc->state_cb) svc->state_cb(call_id, AETHERNET_VOICE_STATE_CONNECTED, svc->state_cb_ud); }
        } else if (strcmp(sig, "video_hangup") == 0) {
            video_call_record_t *rec = vc_find(svc, call_id);
            if (rec) { if (svc->state_cb) svc->state_cb(call_id, AETHERNET_VOICE_STATE_ENDED, svc->state_cb_ud); vc_free(rec); }
        } else if (strcmp(sig, "keyframe_request") == 0) {
            if (svc->kfr_cb) svc->kfr_cb(call_id, svc->kfr_cb_ud);
        } else if (strcmp(sig, "quality_change") == 0) {
            cJSON *jq = cJSON_GetObjectItemCaseSensitive(obj, "quality");
            if (svc->quality_cb && cJSON_IsString(jq)) svc->quality_cb(call_id, jq->valuestring, svc->quality_cb_ud);
        }
        cJSON_Delete(obj);
        return 0;
    }
    return -1;
}

// ═══════════════════════════════════════════════════════════
// WatchTogetherService
// ═══════════════════════════════════════════════════════════

#define WT_MAX_SESSIONS 8
#define WT_MAX_MEMBERS  16

typedef struct {
    uint8_t  session_id[16];
    char    *host_uhid;      // owned
    char    *media_url;      // owned
    char    *members[WT_MAX_MEMBERS];  // owned
    int      member_count;
    int      is_playing;
    int64_t  position_ms;
    double   playback_speed;
    bool     active;
} watch_session_t;

struct aethernet_watch_together_service {
    aethernet_transport_t       *transport;
    aethernet_routing_service_t *routing;
    char                     *local_uhid;

    watch_session_t sessions[WT_MAX_SESSIONS];

    aethernet_watch_invite_cb    invite_cb;
    void                     *invite_cb_ud;
    aethernet_watch_playback_cb  playback_cb;
    void                     *playback_cb_ud;
    aethernet_watch_reaction_cb  reaction_cb;
    void                     *reaction_cb_ud;
    aethernet_watch_member_cb    member_joined_cb;
    void                     *member_joined_cb_ud;
    aethernet_watch_member_cb    member_left_cb;
    void                     *member_left_cb_ud;
};

static watch_session_t *wt_find(aethernet_watch_together_service_t *svc, const uint8_t id[16]) {
    for (int i = 0; i < WT_MAX_SESSIONS; i++)
        if (svc->sessions[i].active && memcmp(svc->sessions[i].session_id, id, 16) == 0) return &svc->sessions[i];
    return NULL;
}

static watch_session_t *wt_alloc(aethernet_watch_together_service_t *svc) {
    for (int i = 0; i < WT_MAX_SESSIONS; i++)
        if (!svc->sessions[i].active) return &svc->sessions[i];
    return NULL;
}

static void wt_free_session(watch_session_t *s) {
    if (!s) return;
    free(s->host_uhid); s->host_uhid = NULL;
    free(s->media_url); s->media_url = NULL;
    for (int i = 0; i < s->member_count; i++) { free(s->members[i]); s->members[i] = NULL; }
    s->member_count = 0;
    s->active = false;
}

static void wt_broadcast(aethernet_watch_together_service_t *svc, watch_session_t *ses, cJSON *obj, uint8_t pkt_type) {
    for (int i = 0; i < ses->member_count; i++) {
        const char *uhid = ses->members[i];
        if (!uhid) continue;
        if (svc->local_uhid && strcmp(uhid, svc->local_uhid) == 0) continue;
        cJSON *dup = cJSON_Duplicate(obj, 1);
        st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, dup, uhid, pkt_type, 32);
    }
    cJSON_Delete(obj);
}

aethernet_watch_together_service_t *aethernet_watch_together_service_create(
    aethernet_transport_t *transport, aethernet_routing_service_t *routing, const char *local_uhid
) {
    if (!transport || !routing || !local_uhid) return NULL;
    aethernet_watch_together_service_t *svc = (aethernet_watch_together_service_t *)calloc(1, sizeof(aethernet_watch_together_service_t));
    if (!svc) return NULL;
    svc->transport  = transport;
    svc->routing    = routing;
    svc->local_uhid = st_str_dup(local_uhid);
    if (!svc->local_uhid) { free(svc); return NULL; }
    return svc;
}

void aethernet_watch_together_service_destroy(aethernet_watch_together_service_t *svc) {
    if (!svc) return;
    for (int i = 0; i < WT_MAX_SESSIONS; i++) wt_free_session(&svc->sessions[i]);
    free(svc->local_uhid);
    free(svc);
}

void aethernet_watch_set_invite_cb(aethernet_watch_together_service_t *svc, aethernet_watch_invite_cb cb, void *ud) { if (svc) { svc->invite_cb = cb; svc->invite_cb_ud = ud; } }
void aethernet_watch_set_playback_cb(aethernet_watch_together_service_t *svc, aethernet_watch_playback_cb cb, void *ud) { if (svc) { svc->playback_cb = cb; svc->playback_cb_ud = ud; } }
void aethernet_watch_set_reaction_cb(aethernet_watch_together_service_t *svc, aethernet_watch_reaction_cb cb, void *ud) { if (svc) { svc->reaction_cb = cb; svc->reaction_cb_ud = ud; } }
void aethernet_watch_set_member_joined_cb(aethernet_watch_together_service_t *svc, aethernet_watch_member_cb cb, void *ud) { if (svc) { svc->member_joined_cb = cb; svc->member_joined_cb_ud = ud; } }
void aethernet_watch_set_member_left_cb(aethernet_watch_together_service_t *svc, aethernet_watch_member_cb cb, void *ud) { if (svc) { svc->member_left_cb = cb; svc->member_left_cb_ud = ud; } }

int aethernet_watch_invite_to_session(
    aethernet_watch_together_service_t *svc, const char **to_uhids, int to_count, const char *media_url, uint8_t session_id_out[16]
) {
    if (!svc || !media_url) return -1;
    watch_session_t *ses = wt_alloc(svc);
    if (!ses) return -1;

    st_gen_uuid_v4(ses->session_id);
    memcpy(session_id_out, ses->session_id, 16);
    ses->host_uhid     = st_str_dup(svc->local_uhid);
    ses->media_url     = st_str_dup(media_url);
    ses->playback_speed = 1.0;
    ses->active        = true;

    ses->members[ses->member_count++] = st_str_dup(svc->local_uhid);
    for (int i = 0; i < to_count && ses->member_count < WT_MAX_MEMBERS; i++) {
        if (to_uhids[i]) ses->members[ses->member_count++] = st_str_dup(to_uhids[i]);
    }

    char id_str[37];
    st_uuid_to_canonical(ses->session_id, id_str);

    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "host_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "media_url", media_url);
    cJSON_AddStringToObject(obj, "signal_type", "watch_invite");
    cJSON *marr = cJSON_CreateArray();
    for (int i = 0; i < ses->member_count; i++) cJSON_AddItemToArray(marr, cJSON_CreateString(ses->members[i]));
    cJSON_AddItemToObject(obj, "members", marr);

    for (int i = 0; i < to_count; i++) {
        if (!to_uhids[i]) continue;
        cJSON *dup = cJSON_Duplicate(obj, 1);
        st_send_json_unicast(svc->transport, svc->routing, svc->local_uhid, dup, to_uhids[i], AETHERNET_PACKET_TYPE_WATCH_SYNC, 32);
    }
    cJSON_Delete(obj);
    return 0;
}

int aethernet_watch_play(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms) {
    if (!svc) return -1;
    watch_session_t *ses = wt_find(svc, session_id);
    if (!ses) return -1;
    ses->is_playing = 1; ses->position_ms = position_ms;
    char id_str[37]; st_uuid_to_canonical(session_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddNumberToObject(obj, "position_ms", (double)position_ms);
    cJSON_AddNumberToObject(obj, "sent_at_ms", (double)st_now_ms());
    cJSON_AddStringToObject(obj, "signal_type", "watch_play");
    wt_broadcast(svc, ses, obj, AETHERNET_PACKET_TYPE_WATCH_SYNC);
    return 0;
}

int aethernet_watch_pause(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms) {
    if (!svc) return -1;
    watch_session_t *ses = wt_find(svc, session_id);
    if (!ses) return -1;
    ses->is_playing = 0; ses->position_ms = position_ms;
    char id_str[37]; st_uuid_to_canonical(session_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddNumberToObject(obj, "position_ms", (double)position_ms);
    cJSON_AddStringToObject(obj, "signal_type", "watch_pause");
    wt_broadcast(svc, ses, obj, AETHERNET_PACKET_TYPE_WATCH_SYNC);
    return 0;
}

int aethernet_watch_seek(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms) {
    if (!svc) return -1;
    watch_session_t *ses = wt_find(svc, session_id);
    if (!ses) return -1;
    ses->position_ms = position_ms;
    char id_str[37]; st_uuid_to_canonical(session_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddNumberToObject(obj, "position_ms", (double)position_ms);
    cJSON_AddNumberToObject(obj, "sent_at_ms", (double)st_now_ms());
    cJSON_AddStringToObject(obj, "signal_type", "watch_seek");
    wt_broadcast(svc, ses, obj, AETHERNET_PACKET_TYPE_WATCH_SYNC);
    return 0;
}

int aethernet_watch_set_speed(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], double speed) {
    if (!svc) return -1;
    watch_session_t *ses = wt_find(svc, session_id);
    if (!ses) return -1;
    ses->playback_speed = speed;
    char id_str[37]; st_uuid_to_canonical(session_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddNumberToObject(obj, "speed", speed);
    cJSON_AddStringToObject(obj, "signal_type", "watch_speed");
    wt_broadcast(svc, ses, obj, AETHERNET_PACKET_TYPE_WATCH_SYNC);
    return 0;
}

int aethernet_watch_send_reaction(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], const char *emoji) {
    if (!svc || !emoji) return -1;
    watch_session_t *ses = wt_find(svc, session_id);
    if (!ses) return -1;
    char id_str[37]; st_uuid_to_canonical(session_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "session_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "emoji", emoji);
    cJSON_AddNumberToObject(obj, "sent_at_ms", (double)st_now_ms());
    wt_broadcast(svc, ses, obj, AETHERNET_PACKET_TYPE_WATCH_REACTION);
    return 0;
}

int aethernet_watch_handle_packet(aethernet_watch_together_service_t *svc, const aethernet_packet_t *packet) {
    if (!svc || !packet) return -1;
    if (packet->type != AETHERNET_PACKET_TYPE_WATCH_SYNC && packet->type != AETHERNET_PACKET_TYPE_WATCH_REACTION) return -1;
    if (!packet->payload || packet->payload_len == 0) return -1;

    cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (!obj) return -1;

    cJSON *jid  = cJSON_GetObjectItemCaseSensitive(obj, "session_id");
    cJSON *jsig = cJSON_GetObjectItemCaseSensitive(obj, "signal_type");
    const char *id_str = cJSON_IsString(jid) ? jid->valuestring : NULL;
    const char *sig    = cJSON_IsString(jsig) ? jsig->valuestring : NULL;

    uint8_t session_id[16] = {0};
    if (!id_str || !st_parse_uuid(id_str, session_id)) { cJSON_Delete(obj); return -1; }

    int64_t now_ms = st_now_ms();

    if (sig && strcmp(sig, "watch_invite") == 0) {
        watch_session_t *ses = wt_alloc(svc);
        if (ses) {
            memcpy(ses->session_id, session_id, 16);
            cJSON *jh = cJSON_GetObjectItemCaseSensitive(obj, "host_uhid");
            cJSON *ju = cJSON_GetObjectItemCaseSensitive(obj, "media_url");
            ses->host_uhid     = st_str_dup(cJSON_IsString(jh) ? jh->valuestring : packet->source_uhid);
            ses->media_url     = st_str_dup(cJSON_IsString(ju) ? ju->valuestring : "");
            ses->playback_speed = 1.0;
            ses->active = true;
            cJSON *marr = cJSON_GetObjectItemCaseSensitive(obj, "members");
            if (cJSON_IsArray(marr)) {
                int mc = cJSON_GetArraySize(marr);
                for (int i = 0; i < mc && ses->member_count < WT_MAX_MEMBERS; i++) {
                    cJSON *mi = cJSON_GetArrayItem(marr, i);
                    if (cJSON_IsString(mi)) ses->members[ses->member_count++] = st_str_dup(mi->valuestring);
                }
            }
            if (svc->invite_cb) svc->invite_cb(session_id, ses->host_uhid, ses->media_url, svc->invite_cb_ud);
        }
    } else if (sig && strcmp(sig, "watch_play") == 0) {
        watch_session_t *ses = wt_find(svc, session_id);
        cJSON *jpos  = cJSON_GetObjectItemCaseSensitive(obj, "position_ms");
        cJSON *jsat  = cJSON_GetObjectItemCaseSensitive(obj, "sent_at_ms");
        int64_t pos = cJSON_IsNumber(jpos) ? (int64_t)jpos->valuedouble : 0;
        int64_t sat = cJSON_IsNumber(jsat) ? (int64_t)jsat->valuedouble : now_ms;
        double speed = ses ? ses->playback_speed : 1.0;
        /* RTT compensation */
        int64_t compensated = pos + (int64_t)((double)(now_ms - sat) * speed);
        if (ses) { ses->is_playing = 1; ses->position_ms = compensated; }
        if (svc->playback_cb) svc->playback_cb(session_id, 1, compensated, svc->playback_cb_ud);
    } else if (sig && strcmp(sig, "watch_pause") == 0) {
        watch_session_t *ses = wt_find(svc, session_id);
        cJSON *jpos = cJSON_GetObjectItemCaseSensitive(obj, "position_ms");
        int64_t pos = cJSON_IsNumber(jpos) ? (int64_t)jpos->valuedouble : 0;
        if (ses) { ses->is_playing = 0; ses->position_ms = pos; }
        if (svc->playback_cb) svc->playback_cb(session_id, 0, pos, svc->playback_cb_ud);
    } else if (sig && strcmp(sig, "watch_seek") == 0) {
        watch_session_t *ses = wt_find(svc, session_id);
        cJSON *jpos = cJSON_GetObjectItemCaseSensitive(obj, "position_ms");
        cJSON *jsat = cJSON_GetObjectItemCaseSensitive(obj, "sent_at_ms");
        int64_t pos = cJSON_IsNumber(jpos) ? (int64_t)jpos->valuedouble : 0;
        int64_t sat = cJSON_IsNumber(jsat) ? (int64_t)jsat->valuedouble : now_ms;
        double speed = ses ? ses->playback_speed : 1.0;
        int64_t compensated = pos + (int64_t)((double)(now_ms - sat) * speed);
        int playing = ses ? ses->is_playing : 0;
        if (ses) ses->position_ms = compensated;
        if (svc->playback_cb) svc->playback_cb(session_id, playing, compensated, svc->playback_cb_ud);
    } else if (sig && strcmp(sig, "watch_speed") == 0) {
        watch_session_t *ses = wt_find(svc, session_id);
        cJSON *jsp = cJSON_GetObjectItemCaseSensitive(obj, "speed");
        if (ses && cJSON_IsNumber(jsp)) ses->playback_speed = jsp->valuedouble;
    } else if (packet->type == AETHERNET_PACKET_TYPE_WATCH_REACTION) {
        cJSON *jfrom  = cJSON_GetObjectItemCaseSensitive(obj, "from_uhid");
        cJSON *jemoji = cJSON_GetObjectItemCaseSensitive(obj, "emoji");
        if (svc->reaction_cb && cJSON_IsString(jfrom) && cJSON_IsString(jemoji)) {
            svc->reaction_cb(session_id, jfrom->valuestring, jemoji->valuestring, svc->reaction_cb_ud);
        }
    }

    cJSON_Delete(obj);
    return 0;
}
