// SPDX-License-Identifier: MIT
// Aether Mesh — 1-to-1 voice call service.
//
// Single-threaded reference implementation. Hosts pumping packets from multiple
// threads must serialise calls behind their own mutex.
//
// JSON signalling is produced/consumed via cJSON. The CMakeLists.txt wires
// cJSON via FetchContent so no system-level dep is required on the CI host.
//
// NOTE: Build verification requires a Linux/macOS host with cmake + libsodium.
// CI on ubuntu-latest is the verification gate.

#include "aethermesh/voice.h"
#include "aethermesh/constants.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#include <cjson/cJSON.h>

// ─── LE write helpers ─────────────────────────────────────
// These are used by both voice.c and streaming.c (static, file-private).

static void write_le_u32(uint8_t *buf, uint32_t v) {
    buf[0] = (uint8_t)(v);
    buf[1] = (uint8_t)(v >> 8);
    buf[2] = (uint8_t)(v >> 16);
    buf[3] = (uint8_t)(v >> 24);
}

static void write_le_i64(uint8_t *buf, int64_t v) {
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

static uint32_t read_le_u32(const uint8_t *buf) {
    return (uint32_t)buf[0]
         | ((uint32_t)buf[1] << 8)
         | ((uint32_t)buf[2] << 16)
         | ((uint32_t)buf[3] << 24);
}

static int64_t read_le_i64(const uint8_t *buf) {
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

static int64_t voice_now_ms(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *voice_str_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void uuid_to_canonical(const uint8_t id[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0],id[1],id[2],id[3],id[4],id[5],id[6],id[7],
        id[8],id[9],id[10],id[11],id[12],id[13],id[14],id[15]);
}

/* Returns true and fills `out` if `hex_str` (32 lowercase hex chars, no dashes)
   is a valid compact UUID hex. Returns false for the 8-4-4-4-12 form — that is
   handled separately via canonical parsing below. */
static bool parse_uuid_compact(const char *s, uint8_t out[16]) {
    if (!s) return false;
    size_t len = strlen(s);
    /* Accept both compact (32 chars) and canonical (36 chars with dashes) */
    if (len == 36) {
        /* strip dashes: positions 8, 13, 18, 23 */
        char compact[33];
        int ci = 0;
        for (int i = 0; i < 36 && ci < 32; i++) {
            if (s[i] == '-') continue;
            compact[ci++] = s[i];
        }
        compact[32] = 0;
        s = compact;
        len = 32;
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

// ─── Frame assembly ───────────────────────────────────────

/**
 * VoiceFrame binary layout:
 *   [16] CallId BE
 *   [4]  Sequence LE
 *   [8]  TimestampMs LE
 *   [1]  IsSilence
 *   [N]  EncodedPayload
 */
static uint8_t *build_voice_frame(
    const uint8_t *call_id,   // 16 bytes
    uint32_t       sequence,
    int64_t        ts_ms,
    int            is_silence,
    const uint8_t *audio,
    size_t         audio_len,
    uint32_t      *out_len
) {
    size_t total = 16 + 4 + 8 + 1 + audio_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;
    memcpy(buf, call_id, 16);
    write_le_u32(buf + 16, sequence);
    write_le_i64(buf + 20, ts_ms);
    buf[28] = (uint8_t)(is_silence ? 1 : 0);
    if (audio && audio_len) memcpy(buf + 29, audio, audio_len);
    *out_len = (uint32_t)total;
    return buf;
}

static bool parse_voice_frame(
    const uint8_t *data, size_t data_len,
    uint8_t call_id_out[16], uint32_t *seq_out, int64_t *ts_out,
    int *is_silence_out, const uint8_t **audio_out, size_t *audio_len_out
) {
    if (data_len < 29) return false;
    memcpy(call_id_out, data, 16);
    *seq_out        = read_le_u32(data + 16);
    *ts_out         = read_le_i64(data + 20);
    *is_silence_out = data[28] ? 1 : 0;
    *audio_out      = data + 29;
    *audio_len_out  = data_len - 29;
    return true;
}

// ─── Call record ──────────────────────────────────────────

#define VOICE_MAX_CALLS 32

typedef struct {
    uint8_t  call_id[16];
    char    *remote_uhid;    // owned
    int      state;          // aethermesh_voice_call_state_t
    uint32_t next_seq;
    bool     active;
} voice_call_record_t;

// ─── Service struct ───────────────────────────────────────

struct aethermesh_voice_service {
    aethermesh_transport_t       *transport;
    aethermesh_routing_service_t *routing;
    char                     *local_uhid;   // owned

    voice_call_record_t calls[VOICE_MAX_CALLS];

    aethermesh_voice_incoming_cb      incoming_cb;
    void                         *incoming_cb_ud;
    aethermesh_voice_state_changed_cb state_changed_cb;
    void                         *state_changed_cb_ud;
    aethermesh_voice_frame_cb         frame_cb;
    void                         *frame_cb_ud;
};

// ─── Helpers ──────────────────────────────────────────────

static voice_call_record_t *find_call(aethermesh_voice_service_t *svc, const uint8_t call_id[16]) {
    for (int i = 0; i < VOICE_MAX_CALLS; i++) {
        if (svc->calls[i].active && memcmp(svc->calls[i].call_id, call_id, 16) == 0)
            return &svc->calls[i];
    }
    return NULL;
}

static voice_call_record_t *alloc_call(aethermesh_voice_service_t *svc) {
    for (int i = 0; i < VOICE_MAX_CALLS; i++) {
        if (!svc->calls[i].active) return &svc->calls[i];
    }
    return NULL;
}

static void free_call(voice_call_record_t *c) {
    if (!c) return;
    free(c->remote_uhid);
    c->remote_uhid = NULL;
    c->active = false;
}

/* Generate a v4 UUID using rand() — good enough for call IDs in the
   reference impl. Hosts that need cryptographic randomness supply their own. */
static void gen_uuid_v4(uint8_t out[16]) {
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(voice_now_ms() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < 16; i++) out[i] = (uint8_t)(rand() & 0xFF);
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

/* Send a JSON signalling packet unicast. `signal_json` is a cJSON object
   (ownership transferred — printed then freed here). */
static void send_signal_json(aethermesh_voice_service_t *svc, cJSON *obj, const char *to_uhid, uint8_t pkt_type) {
    if (!obj || !to_uhid) { cJSON_Delete(obj); return; }
    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return;

    aethermesh_mesh_packet_t *pkt = aethermesh_packet_new();
    if (!pkt) { free(body); return; }
    pkt->type = pkt_type;
    aethermesh_packet_set_source_uhid(pkt, svc->local_uhid);
    aethermesh_packet_set_destination_uhid(pkt, to_uhid);
    pkt->ttl      = AETHERMESH_DEFAULT_TTL;
    pkt->priority = 32;
    aethermesh_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    free(body);

    /* Unicast via routing cache; fall back to broadcast if no route is known. */
    aethermesh_route_entry_t *route = NULL;
    if (aethermesh_routing_find_cached(svc->routing, to_uhid, &route)) {
        svc->transport->vtable->send(svc->transport->handle, route->next_hop_uhid,
            NULL, 0); /* real hosts serialise + send pkt here */
        aethermesh_route_entry_free(route);
    }
    /* NOTE: actual byte-level send is wired by the host layer which serialises
       the packet via aethermesh_packet_serialize(). This reference impl shows the
       signalling logic; transport binding is intentionally host-side. */
    aethermesh_packet_free(pkt);
}

// ─── Public API ───────────────────────────────────────────

aethermesh_voice_service_t *aethermesh_voice_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
) {
    if (!transport || !routing || !local_uhid) return NULL;
    aethermesh_voice_service_t *svc = (aethermesh_voice_service_t *)calloc(1, sizeof(aethermesh_voice_service_t));
    if (!svc) return NULL;
    svc->transport  = transport;
    svc->routing    = routing;
    svc->local_uhid = voice_str_dup(local_uhid);
    if (!svc->local_uhid) { free(svc); return NULL; }
    return svc;
}

void aethermesh_voice_service_destroy(aethermesh_voice_service_t *svc) {
    if (!svc) return;
    for (int i = 0; i < VOICE_MAX_CALLS; i++) {
        if (svc->calls[i].active) free_call(&svc->calls[i]);
    }
    free(svc->local_uhid);
    free(svc);
}

void aethermesh_voice_set_incoming_cb(aethermesh_voice_service_t *svc, aethermesh_voice_incoming_cb cb, void *ud) {
    if (!svc) return;
    svc->incoming_cb    = cb;
    svc->incoming_cb_ud = ud;
}

void aethermesh_voice_set_state_changed_cb(aethermesh_voice_service_t *svc, aethermesh_voice_state_changed_cb cb, void *ud) {
    if (!svc) return;
    svc->state_changed_cb    = cb;
    svc->state_changed_cb_ud = ud;
}

void aethermesh_voice_set_frame_cb(aethermesh_voice_service_t *svc, aethermesh_voice_frame_cb cb, void *ud) {
    if (!svc) return;
    svc->frame_cb    = cb;
    svc->frame_cb_ud = ud;
}

int aethermesh_voice_send_offer(
    aethermesh_voice_service_t *svc,
    const char             *to_uhid,
    const char            **codecs,
    int                     codec_count,
    int                     sample_rate_hz,
    uint8_t                 call_id_out[16]
) {
    if (!svc || !to_uhid) return -1;
    voice_call_record_t *rec = alloc_call(svc);
    if (!rec) return -1;

    gen_uuid_v4(rec->call_id);
    rec->remote_uhid = voice_str_dup(to_uhid);
    if (!rec->remote_uhid) return -1;
    rec->state    = AETHERMESH_VOICE_STATE_OUTGOING;
    rec->next_seq = 0;
    rec->active   = true;
    memcpy(call_id_out, rec->call_id, 16);

    char id_str[37];
    uuid_to_canonical(rec->call_id, id_str);

    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON *arr = cJSON_CreateArray();
    for (int i = 0; i < codec_count; i++) {
        if (codecs[i]) cJSON_AddItemToArray(arr, cJSON_CreateString(codecs[i]));
    }
    cJSON_AddItemToObject(obj, "codecs", arr);
    cJSON_AddNumberToObject(obj, "sample_rate_hz", sample_rate_hz);
    send_signal_json(svc, obj, to_uhid, AETHERMESH_PACKET_TYPE_VOICE_SIGNALING);
    return 0;
}

int aethermesh_voice_accept_call(aethermesh_voice_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    voice_call_record_t *rec = find_call(svc, call_id);
    if (!rec || rec->state != AETHERMESH_VOICE_STATE_INCOMING) return -1;
    rec->state    = AETHERMESH_VOICE_STATE_CONNECTED;
    rec->next_seq = 0;

    char id_str[37];
    uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "accept");
    send_signal_json(svc, obj, rec->remote_uhid, AETHERMESH_PACKET_TYPE_VOICE_SIGNALING);

    if (svc->state_changed_cb)
        svc->state_changed_cb(rec->call_id, AETHERMESH_VOICE_STATE_CONNECTED, svc->state_changed_cb_ud);
    return 0;
}

int aethermesh_voice_hang_up(aethermesh_voice_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    voice_call_record_t *rec = find_call(svc, call_id);
    if (!rec) return -1;

    char id_str[37];
    uuid_to_canonical(rec->call_id, id_str);
    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "call_id", id_str);
    cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
    cJSON_AddStringToObject(obj, "signal_type", "hangup");
    const char *remote = rec->remote_uhid;
    send_signal_json(svc, obj, remote, AETHERMESH_PACKET_TYPE_VOICE_SIGNALING);

    if (svc->state_changed_cb)
        svc->state_changed_cb(rec->call_id, AETHERMESH_VOICE_STATE_ENDED, svc->state_changed_cb_ud);
    free_call(rec);
    return 0;
}

int aethermesh_voice_send_frame(
    aethermesh_voice_service_t *svc,
    const uint8_t          *call_id,
    const uint8_t          *audio,
    size_t                  audio_len,
    int                     is_silence
) {
    if (!svc || !call_id) return -1;
    voice_call_record_t *rec = find_call(svc, call_id);
    if (!rec || rec->state != AETHERMESH_VOICE_STATE_CONNECTED) return -1;

    uint32_t seq    = rec->next_seq++;
    int64_t  ts_ms  = voice_now_ms();
    uint32_t frame_len = 0;
    uint8_t *frame = build_voice_frame(call_id, seq, ts_ms, is_silence, audio, audio_len, &frame_len);
    if (!frame) return -1;

    aethermesh_mesh_packet_t *pkt = aethermesh_packet_new();
    if (!pkt) { free(frame); return -1; }
    pkt->type = AETHERMESH_PACKET_TYPE_VOICE_CALL;
    aethermesh_packet_set_source_uhid(pkt, svc->local_uhid);
    aethermesh_packet_set_destination_uhid(pkt, rec->remote_uhid);
    pkt->ttl      = AETHERMESH_DEFAULT_TTL;
    pkt->priority = 64;
    aethermesh_packet_set_payload(pkt, frame, frame_len);
    free(frame);

    /* Host wires unicast delivery; we just build the packet here. */
    aethermesh_route_entry_t *route = NULL;
    if (aethermesh_routing_find_cached(svc->routing, rec->remote_uhid, &route)) {
        /* Real send: host serialises and transmits via transport->vtable->send */
        aethermesh_route_entry_free(route);
    }
    aethermesh_packet_free(pkt);
    return 0;
}

int aethermesh_voice_handle_packet(aethermesh_voice_service_t *svc, const aethermesh_packet_t *packet) {
    if (!svc || !packet) return -1;

    if (packet->type == AETHERMESH_PACKET_TYPE_VOICE_CALL) {
        /* Binary voice frame */
        if (!packet->payload || packet->payload_len < 29) return -1;
        uint8_t call_id[16];
        uint32_t seq;
        int64_t ts_ms;
        int is_silence;
        const uint8_t *audio;
        size_t audio_len;
        if (!parse_voice_frame(packet->payload, packet->payload_len,
                               call_id, &seq, &ts_ms, &is_silence, &audio, &audio_len))
            return -1;
        if (svc->frame_cb)
            svc->frame_cb(call_id, audio, audio_len, is_silence, ts_ms, svc->frame_cb_ud);
        return 0;
    }

    if (packet->type == AETHERMESH_PACKET_TYPE_VOICE_SIGNALING) {
        if (!packet->payload || packet->payload_len == 0) return -1;
        cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
        if (!obj) return -1;

        cJSON *jid   = cJSON_GetObjectItemCaseSensitive(obj, "call_id");
        cJSON *jsig  = cJSON_GetObjectItemCaseSensitive(obj, "signal_type");
        cJSON *jfrom = cJSON_GetObjectItemCaseSensitive(obj, "from_uhid");

        const char *id_str  = cJSON_IsString(jid)   ? jid->valuestring   : NULL;
        const char *sig     = cJSON_IsString(jsig)  ? jsig->valuestring  : NULL;
        const char *from    = cJSON_IsString(jfrom) ? jfrom->valuestring : packet->source_uhid;

        uint8_t call_id[16] = {0};
        if (!id_str || !parse_uuid_compact(id_str, call_id)) {
            cJSON_Delete(obj);
            return -1;
        }

        if (!sig) {
            /* Offer — no signal_type field means it's the initial offer */
            voice_call_record_t *rec = alloc_call(svc);
            if (rec) {
                memcpy(rec->call_id, call_id, 16);
                rec->remote_uhid = voice_str_dup(from);
                rec->state  = AETHERMESH_VOICE_STATE_INCOMING;
                rec->active = true;

                if (svc->incoming_cb) {
                    /* Gather codecs */
                    cJSON *jarr = cJSON_GetObjectItemCaseSensitive(obj, "codecs");
                    int cc = cJSON_IsArray(jarr) ? cJSON_GetArraySize(jarr) : 0;
                    const char **codecs = NULL;
                    if (cc > 0) {
                        codecs = (const char **)malloc(sizeof(char *) * (size_t)cc);
                        if (codecs) {
                            for (int i = 0; i < cc; i++) {
                                cJSON *ci = cJSON_GetArrayItem(jarr, i);
                                codecs[i] = cJSON_IsString(ci) ? ci->valuestring : "";
                            }
                        }
                    }
                    int sr = 0;
                    cJSON *jsr = cJSON_GetObjectItemCaseSensitive(obj, "sample_rate_hz");
                    if (cJSON_IsNumber(jsr)) sr = (int)jsr->valuedouble;
                    svc->incoming_cb(call_id, from, codecs, cc, sr, svc->incoming_cb_ud);
                    free(codecs);
                }
            }
        } else if (strcmp(sig, "accept") == 0) {
            voice_call_record_t *rec = find_call(svc, call_id);
            if (rec) {
                rec->state = AETHERMESH_VOICE_STATE_CONNECTED;
                if (svc->state_changed_cb)
                    svc->state_changed_cb(call_id, AETHERMESH_VOICE_STATE_CONNECTED, svc->state_changed_cb_ud);
            }
        } else if (strcmp(sig, "hangup") == 0) {
            voice_call_record_t *rec = find_call(svc, call_id);
            if (rec) {
                if (svc->state_changed_cb)
                    svc->state_changed_cb(call_id, AETHERMESH_VOICE_STATE_ENDED, svc->state_changed_cb_ud);
                free_call(rec);
            }
        }

        cJSON_Delete(obj);
        return 0;
    }

    return -1;
}
