// SPDX-License-Identifier: MIT
// Aether Mesh — Group voice call service.
//
// Single-threaded reference implementation. See voice.c for LE helpers and
// UUID utilities (those are duplicated here since each .c file is self-contained).
//
// NOTE: Build verification requires Linux/macOS with cmake + libsodium + cJSON.
// CI on ubuntu-latest is the verification gate.

#include "aethermesh/voice.h"
#include "aethermesh/constants.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#include <cjson/cJSON.h>

// ─── LE helpers (file-private) ────────────────────────────

static void gv_write_le_u32(uint8_t *buf, uint32_t v) {
    buf[0] = (uint8_t)(v);
    buf[1] = (uint8_t)(v >> 8);
    buf[2] = (uint8_t)(v >> 16);
    buf[3] = (uint8_t)(v >> 24);
}

static void gv_write_le_i64(uint8_t *buf, int64_t v) {
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

static uint32_t gv_read_le_u32(const uint8_t *buf) {
    return (uint32_t)buf[0]
         | ((uint32_t)buf[1] << 8)
         | ((uint32_t)buf[2] << 16)
         | ((uint32_t)buf[3] << 24);
}

static int64_t gv_read_le_i64(const uint8_t *buf) {
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

static int64_t gv_now_ms(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *gv_str_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void gv_uuid_to_canonical(const uint8_t id[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0],id[1],id[2],id[3],id[4],id[5],id[6],id[7],
        id[8],id[9],id[10],id[11],id[12],id[13],id[14],id[15]);
}

static bool gv_parse_uuid(const char *s, uint8_t out[16]) {
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

static void gv_gen_uuid_v4(uint8_t out[16]) {
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(gv_now_ms() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < 16; i++) out[i] = (uint8_t)(rand() & 0xFF);
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// ─── GroupVoiceFrame binary layout ───────────────────────
// [16] CallId BE
// [4]  Sequence LE
// [8]  TimestampMs LE
// [1]  IsSilence
// [4]  KeyGeneration LE
// [N]  EncodedPayload

static uint8_t *build_group_voice_frame(
    const uint8_t *call_id,
    uint32_t       sequence,
    int64_t        ts_ms,
    int            is_silence,
    uint32_t       key_generation,
    const uint8_t *audio,
    size_t         audio_len,
    uint32_t      *out_len
) {
    size_t total = 16 + 4 + 8 + 1 + 4 + audio_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;
    memcpy(buf, call_id, 16);
    gv_write_le_u32(buf + 16, sequence);
    gv_write_le_i64(buf + 20, ts_ms);
    buf[28] = (uint8_t)(is_silence ? 1 : 0);
    gv_write_le_u32(buf + 29, key_generation);
    if (audio && audio_len) memcpy(buf + 33, audio, audio_len);
    *out_len = (uint32_t)total;
    return buf;
}

static bool parse_group_voice_frame(
    const uint8_t *data, size_t data_len,
    uint8_t call_id_out[16], uint32_t *seq_out, int64_t *ts_out,
    int *is_silence_out, uint32_t *key_gen_out,
    const uint8_t **audio_out, size_t *audio_len_out
) {
    if (data_len < 33) return false;
    memcpy(call_id_out, data, 16);
    *seq_out         = gv_read_le_u32(data + 16);
    *ts_out          = gv_read_le_i64(data + 20);
    *is_silence_out  = data[28] ? 1 : 0;
    *key_gen_out     = gv_read_le_u32(data + 29);
    *audio_out       = data + 33;
    *audio_len_out   = data_len - 33;
    return true;
}

// ─── Member list ──────────────────────────────────────────

#define GV_MAX_MEMBERS 8     /* matches ProtocolConstants.maxGroupVoiceMembers */
#define GV_MAX_CALLS   16

typedef struct {
    uint8_t  call_id[16];
    char    *member_uhids[GV_MAX_MEMBERS];  // owned
    int      member_count;
    char    *codecs[8];                     // owned, up to 8 codecs
    int      codec_count;
    uint32_t next_seq;
    bool     active;
} group_call_record_t;

// ─── Service struct ───────────────────────────────────────

struct aethermesh_group_voice_service {
    aethermesh_transport_t       *transport;
    aethermesh_routing_service_t *routing;
    char                     *local_uhid;   // owned

    group_call_record_t calls[GV_MAX_CALLS];

    aethermesh_group_voice_invite_cb invite_cb;
    void                        *invite_cb_ud;
    aethermesh_group_voice_member_cb member_joined_cb;
    void                        *member_joined_cb_ud;
    aethermesh_group_voice_member_cb member_left_cb;
    void                        *member_left_cb_ud;
    aethermesh_group_voice_frame_cb  frame_cb;
    void                        *frame_cb_ud;
};

// ─── Internal helpers ─────────────────────────────────────

static group_call_record_t *gv_find_call(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16]) {
    for (int i = 0; i < GV_MAX_CALLS; i++) {
        if (svc->calls[i].active && memcmp(svc->calls[i].call_id, call_id, 16) == 0)
            return &svc->calls[i];
    }
    return NULL;
}

static group_call_record_t *gv_alloc_call(aethermesh_group_voice_service_t *svc) {
    for (int i = 0; i < GV_MAX_CALLS; i++) {
        if (!svc->calls[i].active) return &svc->calls[i];
    }
    return NULL;
}

static void gv_free_call(group_call_record_t *c) {
    if (!c) return;
    for (int i = 0; i < c->member_count; i++) { free(c->member_uhids[i]); c->member_uhids[i] = NULL; }
    c->member_count = 0;
    for (int i = 0; i < c->codec_count; i++) { free(c->codecs[i]); c->codecs[i] = NULL; }
    c->codec_count = 0;
    c->active = false;
}

static void gv_send_signal_to(aethermesh_group_voice_service_t *svc, cJSON *obj, const char *to_uhid) {
    if (!obj || !to_uhid) { cJSON_Delete(obj); return; }
    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return;

    aethermesh_mesh_packet_t *pkt = aethermesh_packet_new();
    if (!pkt) { free(body); return; }
    pkt->type = AETHERMESH_PACKET_TYPE_VOICE_SIGNALING;
    aethermesh_packet_set_source_uhid(pkt, svc->local_uhid);
    aethermesh_packet_set_destination_uhid(pkt, to_uhid);
    pkt->ttl      = AETHERMESH_DEFAULT_TTL;
    pkt->priority = 32;
    aethermesh_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    free(body);

    /* Routing lookup; actual serialise+send is host responsibility. */
    aethermesh_route_entry_t *route = NULL;
    if (aethermesh_routing_find_cached(svc->routing, to_uhid, &route)) {
        aethermesh_route_entry_free(route);
    }
    aethermesh_packet_free(pkt);
}

static void gv_broadcast_to_members(aethermesh_group_voice_service_t *svc, group_call_record_t *rec, cJSON *obj) {
    /* cJSON_Duplicate so we can send to multiple peers without freeing mid-loop */
    for (int i = 0; i < rec->member_count; i++) {
        if (!rec->member_uhids[i]) continue;
        if (svc->local_uhid && strcmp(rec->member_uhids[i], svc->local_uhid) == 0) continue;
        cJSON *dup = cJSON_Duplicate(obj, 1 /* recurse */);
        gv_send_signal_to(svc, dup, rec->member_uhids[i]);
    }
    cJSON_Delete(obj);
}

// ─── Public API ───────────────────────────────────────────

aethermesh_group_voice_service_t *aethermesh_group_voice_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
) {
    if (!transport || !routing || !local_uhid) return NULL;
    aethermesh_group_voice_service_t *svc = (aethermesh_group_voice_service_t *)calloc(1, sizeof(aethermesh_group_voice_service_t));
    if (!svc) return NULL;
    svc->transport  = transport;
    svc->routing    = routing;
    svc->local_uhid = gv_str_dup(local_uhid);
    if (!svc->local_uhid) { free(svc); return NULL; }
    return svc;
}

void aethermesh_group_voice_service_destroy(aethermesh_group_voice_service_t *svc) {
    if (!svc) return;
    for (int i = 0; i < GV_MAX_CALLS; i++) {
        if (svc->calls[i].active) gv_free_call(&svc->calls[i]);
    }
    free(svc->local_uhid);
    free(svc);
}

void aethermesh_group_voice_set_invite_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_invite_cb cb, void *ud) {
    if (svc) { svc->invite_cb = cb; svc->invite_cb_ud = ud; }
}
void aethermesh_group_voice_set_member_joined_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_member_cb cb, void *ud) {
    if (svc) { svc->member_joined_cb = cb; svc->member_joined_cb_ud = ud; }
}
void aethermesh_group_voice_set_member_left_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_member_cb cb, void *ud) {
    if (svc) { svc->member_left_cb = cb; svc->member_left_cb_ud = ud; }
}
void aethermesh_group_voice_set_frame_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_frame_cb cb, void *ud) {
    if (svc) { svc->frame_cb = cb; svc->frame_cb_ud = ud; }
}

int aethermesh_group_voice_invite(
    aethermesh_group_voice_service_t *svc,
    const char                  **to_uhids,
    int                           to_count,
    const char                  **codecs,
    int                           codec_count,
    uint8_t                       call_id_out[16]
) {
    if (!svc) return -1;
    group_call_record_t *rec = gv_alloc_call(svc);
    if (!rec) return -1;

    gv_gen_uuid_v4(rec->call_id);
    memcpy(call_id_out, rec->call_id, 16);
    rec->active   = true;
    rec->next_seq = 0;

    /* Add self */
    rec->member_uhids[rec->member_count++] = gv_str_dup(svc->local_uhid);

    /* Add invitees */
    for (int i = 0; i < to_count && rec->member_count < GV_MAX_MEMBERS; i++) {
        if (to_uhids[i]) rec->member_uhids[rec->member_count++] = gv_str_dup(to_uhids[i]);
    }

    /* Copy codecs */
    for (int i = 0; i < codec_count && i < 8; i++) {
        if (codecs[i]) rec->codecs[rec->codec_count++] = gv_str_dup(codecs[i]);
    }

    char id_str[37];
    gv_uuid_to_canonical(rec->call_id, id_str);

    /* Build invite JSON and send to each invitee */
    for (int j = 0; j < to_count; j++) {
        if (!to_uhids[j]) continue;
        cJSON *obj = cJSON_CreateObject();
        cJSON_AddStringToObject(obj, "call_id", id_str);
        cJSON_AddStringToObject(obj, "from_uhid", svc->local_uhid);
        cJSON_AddStringToObject(obj, "signal_type", "group_invite");
        cJSON *carr = cJSON_CreateArray();
        for (int k = 0; k < rec->codec_count; k++)
            cJSON_AddItemToArray(carr, cJSON_CreateString(rec->codecs[k]));
        cJSON_AddItemToObject(obj, "codecs", carr);
        cJSON *marr = cJSON_CreateArray();
        for (int k = 0; k < rec->member_count; k++)
            cJSON_AddItemToArray(marr, cJSON_CreateString(rec->member_uhids[k]));
        cJSON_AddItemToObject(obj, "members", marr);
        gv_send_signal_to(svc, obj, to_uhids[j]);
    }
    return 0;
}

int aethermesh_group_voice_join(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    group_call_record_t *rec = gv_find_call(svc, call_id);
    if (!rec) return -1;

    /* Add self if not already in list */
    bool already = false;
    for (int i = 0; i < rec->member_count; i++) {
        if (rec->member_uhids[i] && strcmp(rec->member_uhids[i], svc->local_uhid) == 0) { already = true; break; }
    }
    if (!already && rec->member_count < GV_MAX_MEMBERS) {
        rec->member_uhids[rec->member_count++] = gv_str_dup(svc->local_uhid);
    }
    rec->next_seq = 0;

    char id_str[37];
    gv_uuid_to_canonical(rec->call_id, id_str);

    /* Broadcast join */
    cJSON *base = cJSON_CreateObject();
    cJSON_AddStringToObject(base, "call_id", id_str);
    cJSON_AddStringToObject(base, "uhid", svc->local_uhid);
    cJSON_AddStringToObject(base, "signal_type", "group_join");
    gv_broadcast_to_members(svc, rec, base);

    if (svc->member_joined_cb) svc->member_joined_cb(call_id, svc->local_uhid, svc->member_joined_cb_ud);
    return 0;
}

int aethermesh_group_voice_leave(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16]) {
    if (!svc) return -1;
    group_call_record_t *rec = gv_find_call(svc, call_id);
    if (!rec) return -1;

    char id_str[37];
    gv_uuid_to_canonical(rec->call_id, id_str);

    cJSON *base = cJSON_CreateObject();
    cJSON_AddStringToObject(base, "call_id", id_str);
    cJSON_AddStringToObject(base, "uhid", svc->local_uhid);
    cJSON_AddStringToObject(base, "signal_type", "group_leave");
    gv_broadcast_to_members(svc, rec, base);

    if (svc->member_left_cb) svc->member_left_cb(call_id, svc->local_uhid, svc->member_left_cb_ud);
    gv_free_call(rec);
    return 0;
}

int aethermesh_group_voice_kick(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16], const char *uhid) {
    if (!svc || !uhid) return -1;
    group_call_record_t *rec = gv_find_call(svc, call_id);
    if (!rec) return -1;

    char id_str[37];
    gv_uuid_to_canonical(rec->call_id, id_str);

    cJSON *base = cJSON_CreateObject();
    cJSON_AddStringToObject(base, "call_id", id_str);
    cJSON_AddStringToObject(base, "kicked_uhid", uhid);
    cJSON_AddStringToObject(base, "by_uhid", svc->local_uhid);
    cJSON_AddStringToObject(base, "signal_type", "group_kick");

    /* Notify the kicked peer directly */
    cJSON *dup = cJSON_Duplicate(base, 1);
    gv_send_signal_to(svc, dup, uhid);

    /* Remove from list then notify remaining members */
    for (int i = 0; i < rec->member_count; i++) {
        if (rec->member_uhids[i] && strcmp(rec->member_uhids[i], uhid) == 0) {
            free(rec->member_uhids[i]);
            rec->member_uhids[i] = rec->member_uhids[--rec->member_count];
            rec->member_uhids[rec->member_count] = NULL;
            break;
        }
    }
    gv_broadcast_to_members(svc, rec, base);

    if (svc->member_left_cb) svc->member_left_cb(call_id, uhid, svc->member_left_cb_ud);
    return 0;
}

int aethermesh_group_voice_send_frame(
    aethermesh_group_voice_service_t *svc,
    const uint8_t                *call_id,
    const uint8_t                *audio,
    size_t                        audio_len,
    int                           is_silence,
    uint32_t                      key_generation
) {
    if (!svc || !call_id) return -1;
    group_call_record_t *rec = gv_find_call(svc, call_id);
    if (!rec) return -1;

    uint32_t seq   = rec->next_seq++;
    int64_t  ts_ms = gv_now_ms();
    uint32_t frame_len = 0;
    uint8_t *frame = build_group_voice_frame(call_id, seq, ts_ms, is_silence, key_generation, audio, audio_len, &frame_len);
    if (!frame) return -1;

    for (int i = 0; i < rec->member_count; i++) {
        const char *uhid = rec->member_uhids[i];
        if (!uhid) continue;
        if (svc->local_uhid && strcmp(uhid, svc->local_uhid) == 0) continue;

        aethermesh_mesh_packet_t *pkt = aethermesh_packet_new();
        if (!pkt) continue;
        pkt->type = AETHERMESH_PACKET_TYPE_VOICE_CALL;
        aethermesh_packet_set_source_uhid(pkt, svc->local_uhid);
        aethermesh_packet_set_destination_uhid(pkt, uhid);
        pkt->ttl      = AETHERMESH_DEFAULT_TTL;
        pkt->priority = 64;
        aethermesh_packet_set_payload(pkt, frame, frame_len);

        aethermesh_route_entry_t *route = NULL;
        if (aethermesh_routing_find_cached(svc->routing, uhid, &route)) {
            /* Host serialises and transmits */
            aethermesh_route_entry_free(route);
        }
        aethermesh_packet_free(pkt);
    }
    free(frame);
    return 0;
}

int aethermesh_group_voice_handle_packet(aethermesh_group_voice_service_t *svc, const aethermesh_packet_t *packet) {
    if (!svc || !packet) return -1;

    if (packet->type == AETHERMESH_PACKET_TYPE_VOICE_CALL) {
        if (!packet->payload || packet->payload_len < 33) return -1;
        uint8_t  call_id[16];
        uint32_t seq, key_gen;
        int64_t  ts_ms;
        int      is_silence;
        const uint8_t *audio;
        size_t audio_len;
        if (!parse_group_voice_frame(packet->payload, packet->payload_len,
                                     call_id, &seq, &ts_ms, &is_silence, &key_gen,
                                     &audio, &audio_len)) return -1;
        if (svc->frame_cb)
            svc->frame_cb(call_id, packet->source_uhid, audio, audio_len, is_silence, key_gen, ts_ms, svc->frame_cb_ud);
        return 0;
    }

    if (packet->type == AETHERMESH_PACKET_TYPE_VOICE_SIGNALING) {
        if (!packet->payload || packet->payload_len == 0) return -1;
        cJSON *obj = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
        if (!obj) return -1;

        cJSON *jid  = cJSON_GetObjectItemCaseSensitive(obj, "call_id");
        cJSON *jsig = cJSON_GetObjectItemCaseSensitive(obj, "signal_type");
        const char *id_str = cJSON_IsString(jid)  ? jid->valuestring  : NULL;
        const char *sig    = cJSON_IsString(jsig) ? jsig->valuestring : NULL;

        uint8_t call_id[16] = {0};
        if (!id_str || !gv_parse_uuid(id_str, call_id)) { cJSON_Delete(obj); return -1; }

        if (sig && strcmp(sig, "group_invite") == 0) {
            group_call_record_t *rec = gv_alloc_call(svc);
            if (rec) {
                memcpy(rec->call_id, call_id, 16);
                rec->active   = true;
                rec->next_seq = 0;

                cJSON *marr = cJSON_GetObjectItemCaseSensitive(obj, "members");
                if (cJSON_IsArray(marr)) {
                    int mc = cJSON_GetArraySize(marr);
                    for (int i = 0; i < mc && rec->member_count < GV_MAX_MEMBERS; i++) {
                        cJSON *mi = cJSON_GetArrayItem(marr, i);
                        if (cJSON_IsString(mi)) rec->member_uhids[rec->member_count++] = gv_str_dup(mi->valuestring);
                    }
                }
                cJSON *carr = cJSON_GetObjectItemCaseSensitive(obj, "codecs");
                if (cJSON_IsArray(carr)) {
                    int cc = cJSON_GetArraySize(carr);
                    for (int i = 0; i < cc && rec->codec_count < 8; i++) {
                        cJSON *ci = cJSON_GetArrayItem(carr, i);
                        if (cJSON_IsString(ci)) rec->codecs[rec->codec_count++] = gv_str_dup(ci->valuestring);
                    }
                }
                cJSON *jfrom = cJSON_GetObjectItemCaseSensitive(obj, "from_uhid");
                const char *from = cJSON_IsString(jfrom) ? jfrom->valuestring : packet->source_uhid;

                if (svc->invite_cb) {
                    const char **codecs = NULL;
                    if (rec->codec_count > 0) {
                        codecs = (const char **)malloc(sizeof(char *) * (size_t)rec->codec_count);
                        if (codecs) for (int i = 0; i < rec->codec_count; i++) codecs[i] = rec->codecs[i];
                    }
                    svc->invite_cb(call_id, from, codecs, rec->codec_count, svc->invite_cb_ud);
                    free(codecs);
                }
            }
        } else if (sig && strcmp(sig, "group_join") == 0) {
            cJSON *juhid = cJSON_GetObjectItemCaseSensitive(obj, "uhid");
            const char *uhid = cJSON_IsString(juhid) ? juhid->valuestring : packet->source_uhid;
            group_call_record_t *rec = gv_find_call(svc, call_id);
            if (rec && uhid) {
                bool already = false;
                for (int i = 0; i < rec->member_count; i++)
                    if (rec->member_uhids[i] && strcmp(rec->member_uhids[i], uhid) == 0) { already = true; break; }
                if (!already && rec->member_count < GV_MAX_MEMBERS)
                    rec->member_uhids[rec->member_count++] = gv_str_dup(uhid);
                if (svc->member_joined_cb) svc->member_joined_cb(call_id, uhid, svc->member_joined_cb_ud);
            }
        } else if (sig && strcmp(sig, "group_leave") == 0) {
            cJSON *juhid = cJSON_GetObjectItemCaseSensitive(obj, "uhid");
            const char *uhid = cJSON_IsString(juhid) ? juhid->valuestring : packet->source_uhid;
            group_call_record_t *rec = gv_find_call(svc, call_id);
            if (rec && uhid) {
                for (int i = 0; i < rec->member_count; i++) {
                    if (rec->member_uhids[i] && strcmp(rec->member_uhids[i], uhid) == 0) {
                        free(rec->member_uhids[i]);
                        rec->member_uhids[i] = rec->member_uhids[--rec->member_count];
                        rec->member_uhids[rec->member_count] = NULL;
                        break;
                    }
                }
                if (svc->member_left_cb) svc->member_left_cb(call_id, uhid, svc->member_left_cb_ud);
            }
        } else if (sig && strcmp(sig, "group_kick") == 0) {
            cJSON *jkicked = cJSON_GetObjectItemCaseSensitive(obj, "kicked_uhid");
            const char *kicked = cJSON_IsString(jkicked) ? jkicked->valuestring : NULL;
            if (kicked) {
                group_call_record_t *rec = gv_find_call(svc, call_id);
                bool am_kicked = svc->local_uhid && strcmp(kicked, svc->local_uhid) == 0;
                if (am_kicked) {
                    if (rec) gv_free_call(rec);
                    if (svc->member_left_cb) svc->member_left_cb(call_id, svc->local_uhid, svc->member_left_cb_ud);
                } else if (rec) {
                    for (int i = 0; i < rec->member_count; i++) {
                        if (rec->member_uhids[i] && strcmp(rec->member_uhids[i], kicked) == 0) {
                            free(rec->member_uhids[i]);
                            rec->member_uhids[i] = rec->member_uhids[--rec->member_count];
                            rec->member_uhids[rec->member_count] = NULL;
                            break;
                        }
                    }
                    if (svc->member_left_cb) svc->member_left_cb(call_id, kicked, svc->member_left_cb_ud);
                }
            }
        }

        cJSON_Delete(obj);
        return 0;
    }

    return -1;
}
