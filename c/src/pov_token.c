// SPDX-License-Identifier: MIT
// AetherNet Market — Proof-of-Vicinity token (PoVTokenExchange = 43).
//
// C port of AetherNet.Market PoVToken / PoVTokenCodec / PoVTokenExchangeService. See
// include/aethernet/pov_token.h for the contract. Byte-identical to the C# reference
// and every other language implementation, proven against
// fixtures/market/pov_token_basic.json.

#include "aethernet/pov_token.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <cjson/cJSON.h>

#include "aethernet/security.h"   /* aethernet_ed25519_sign / _verify */
/* AETHERNET_MAX_UHID_LEN arrives via aethernet/protocol.h -> constants.h
   (the project's transitive-include convention). */

// ── small helpers ─────────────────────────────────────────────────────────

static char *dup_str(const char *s) {
    if (!s) s = "";
    size_t n = strlen(s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n + 1);
    return out;
}

static bool set_field(char **field, const char *value) {
    char *copy = dup_str(value);
    if (!copy) return false;
    free(*field);
    *field = copy;
    return true;
}

static void put_u32_le(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

static void put_i64_le(uint8_t *p, int64_t v) {
    uint64_t u = (uint64_t)v;
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)((u >> (8 * i)) & 0xFF);
}

static int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static int hex_byte(const char *s) {
    int hi = hex_nibble(s[0]);
    int lo = hex_nibble(s[1]);
    if (hi < 0 || lo < 0) return -1;
    return (hi << 4) | lo;
}

static void bytes_to_hex(const uint8_t *bytes, size_t len, char *out) {
    static const char hexd[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = hexd[(bytes[i] >> 4) & 0xF];
        out[i * 2 + 1] = hexd[bytes[i] & 0xF];
    }
    out[len * 2] = '\0';
}

// Extract the integer value of a top-level "key": <int> from raw JSON text, with
// FULL int64 precision (cJSON stores numbers as double, which silently truncates
// values above 2^53 — e.g. .NET DateTime.Ticks ~6.4e17 — so we must read the
// integer straight from the source text). Returns true on success.
static bool extract_i64_from_json(const char *json, const char *key, int64_t *out) {
    char needle[128];
    int needle_n = snprintf(needle, sizeof(needle), "\"%s\"", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += needle_n;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    if (*p != ':') return false;
    p++;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return false;
    *out = (int64_t)v;
    return true;
}

// ── transport ────────────────────────────────────────────────────────────

bool aethernet_pov_transport_is_short_range(aethernet_pov_transport_t transport) {
    switch (transport) {
        case AETHERNET_POV_TRANSPORT_BLE:
        case AETHERNET_POV_TRANSPORT_NFC:
        case AETHERNET_POV_TRANSPORT_NEARLINK:
            return true;
        default:
            return false;
    }
}

const char *aethernet_pov_transport_name(aethernet_pov_transport_t transport) {
    switch (transport) {
        case AETHERNET_POV_TRANSPORT_BLE:      return "ble";
        case AETHERNET_POV_TRANSPORT_NFC:      return "nfc";
        case AETHERNET_POV_TRANSPORT_NEARLINK: return "nearlink";
        default:                               return "unknown";
    }
}

// ── lifecycle ───────────────────────────────────────────────────────────────

void aethernet_pov_token_init(aethernet_pov_token_t *token) {
    if (!token) return;
    memset(token, 0, sizeof(*token));
    token->transport_used = AETHERNET_POV_TRANSPORT_BLE;
}

void aethernet_pov_token_free_fields(aethernet_pov_token_t *token) {
    if (!token) return;
    free(token->witness_uhid);
    free(token->subject_uhid);
    memset(token, 0, sizeof(*token));
    token->transport_used = AETHERNET_POV_TRANSPORT_BLE;
}

bool aethernet_pov_token_set_witness(aethernet_pov_token_t *token, const char *uhid) {
    if (!token) return false;
    return set_field(&token->witness_uhid, uhid);
}

bool aethernet_pov_token_set_subject(aethernet_pov_token_t *token, const char *uhid) {
    if (!token) return false;
    return set_field(&token->subject_uhid, uhid);
}

// ── canonical signable body ─────────────────────────────────────────────────

uint8_t *aethernet_pov_token_build_signable(const char *subject_uhid,
                                            int64_t timestamp_ticks,
                                            aethernet_pov_transport_t transport,
                                            size_t *out_len) {
    if (!out_len) return NULL;
    if (!subject_uhid) subject_uhid = "";

    size_t subject_len = strlen(subject_uhid);
    size_t total = 4 + subject_len + 8 + 1;

    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;

    size_t off = 0;
    put_u32_le(buf + off, (uint32_t)subject_len);   off += 4;
    memcpy(buf + off, subject_uhid, subject_len);    off += subject_len;
    put_i64_le(buf + off, timestamp_ticks);          off += 8;
    buf[off] = (uint8_t)transport;                   off += 1;

    *out_len = off;
    return buf;
}

uint8_t *aethernet_pov_token_signable(const aethernet_pov_token_t *token, size_t *out_len) {
    if (!token) return NULL;
    return aethernet_pov_token_build_signable(token->subject_uhid, token->timestamp_ticks,
                                              token->transport_used, out_len);
}

// ── sign / verify ───────────────────────────────────────────────────────────

static bool sign_into(const aethernet_pov_token_t *token, const uint8_t *private_key, uint8_t *out_sig) {
    size_t len = 0;
    uint8_t *body = aethernet_pov_token_signable(token, &len);
    if (!body) return false;
    bool ok = aethernet_ed25519_sign(private_key, body, len, out_sig);
    aethernet_zeroize(body, len);
    free(body);
    return ok;
}

bool aethernet_pov_token_sign_witness(aethernet_pov_token_t *token, const uint8_t *private_key) {
    if (!token || !private_key) return false;
    uint8_t sig[AETHERNET_POV_SIGNATURE_SIZE];
    if (!sign_into(token, private_key, sig)) return false;
    memcpy(token->witness_signature, sig, AETHERNET_POV_SIGNATURE_SIZE);
    token->witness_signature_len = AETHERNET_POV_SIGNATURE_SIZE;
    return true;
}

bool aethernet_pov_token_sign_subject(aethernet_pov_token_t *token, const uint8_t *private_key) {
    if (!token || !private_key) return false;
    uint8_t sig[AETHERNET_POV_SIGNATURE_SIZE];
    if (!sign_into(token, private_key, sig)) return false;
    memcpy(token->subject_signature, sig, AETHERNET_POV_SIGNATURE_SIZE);
    token->subject_signature_len = AETHERNET_POV_SIGNATURE_SIZE;
    return true;
}

static bool verify_sig(const aethernet_pov_token_t *token, const uint8_t *public_key,
                       const uint8_t *sig, size_t sig_len) {
    if (!token || !public_key) return false;
    if (sig_len != AETHERNET_POV_SIGNATURE_SIZE) return false;
    size_t len = 0;
    uint8_t *body = aethernet_pov_token_signable(token, &len);
    if (!body) return false;
    bool ok = aethernet_ed25519_verify(public_key, body, len, sig);
    free(body);
    return ok;
}

bool aethernet_pov_token_verify_witness(const aethernet_pov_token_t *token, const uint8_t *public_key) {
    if (!token) return false;
    return verify_sig(token, public_key, token->witness_signature, token->witness_signature_len);
}

bool aethernet_pov_token_verify_subject(const aethernet_pov_token_t *token, const uint8_t *public_key) {
    if (!token) return false;
    return verify_sig(token, public_key, token->subject_signature, token->subject_signature_len);
}

// ── JSON wire form ──────────────────────────────────────────────────────────

char *aethernet_pov_token_to_json(const aethernet_pov_token_t *token) {
    if (!token) return NULL;

    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;

    cJSON_AddStringToObject(obj, "witness_uhid", token->witness_uhid ? token->witness_uhid : "");
    cJSON_AddStringToObject(obj, "subject_uhid", token->subject_uhid ? token->subject_uhid : "");
    // timestamp_ticks is a .NET DateTime.Ticks i64 (~6.4e17) — emit it as a raw,
    // exact integer token. cJSON's number type is a double and would round-trip it
    // lossily (truncating above 2^53), so we never route it through AddNumber.
    {
        char ticks_str[24];
        snprintf(ticks_str, sizeof(ticks_str), "%lld", (long long)token->timestamp_ticks);
        cJSON_AddRawToObject(obj, "timestamp_ticks", ticks_str);
    }
    cJSON_AddNumberToObject(obj, "transport_used", (double)token->transport_used);

    char hex[AETHERNET_POV_SIGNATURE_SIZE * 2 + 1];
    if (token->witness_signature_len == AETHERNET_POV_SIGNATURE_SIZE) {
        bytes_to_hex(token->witness_signature, AETHERNET_POV_SIGNATURE_SIZE, hex);
        cJSON_AddStringToObject(obj, "witness_signature", hex);
    }
    if (token->subject_signature_len == AETHERNET_POV_SIGNATURE_SIZE) {
        bytes_to_hex(token->subject_signature, AETHERNET_POV_SIGNATURE_SIZE, hex);
        cJSON_AddStringToObject(obj, "subject_signature", hex);
    }

    char *out = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    return out;
}

static bool parse_sig_hex(const cJSON *jsig, uint8_t *out, size_t *out_len) {
    if (!cJSON_IsString(jsig)) return false;
    const char *h = jsig->valuestring;
    if (strlen(h) != AETHERNET_POV_SIGNATURE_SIZE * 2) return false;
    for (size_t i = 0; i < AETHERNET_POV_SIGNATURE_SIZE; i++) {
        int b = hex_byte(h + i * 2);
        if (b < 0) return false;
        out[i] = (uint8_t)b;
    }
    *out_len = AETHERNET_POV_SIGNATURE_SIZE;
    return true;
}

bool aethernet_pov_token_from_json(const char *json, size_t json_len, aethernet_pov_token_t *out_token) {
    if (!json || !out_token) return false;

    cJSON *obj = cJSON_ParseWithLength(json, json_len);
    if (!obj) return false;

    aethernet_pov_token_init(out_token);
    bool ok = true;

    const cJSON *jw  = cJSON_GetObjectItemCaseSensitive(obj, "witness_uhid");
    const cJSON *js  = cJSON_GetObjectItemCaseSensitive(obj, "subject_uhid");
    const cJSON *jt  = cJSON_GetObjectItemCaseSensitive(obj, "timestamp_ticks");
    const cJSON *jtr = cJSON_GetObjectItemCaseSensitive(obj, "transport_used");
    const cJSON *jws = cJSON_GetObjectItemCaseSensitive(obj, "witness_signature");
    const cJSON *jss = cJSON_GetObjectItemCaseSensitive(obj, "subject_signature");

    if (!cJSON_IsString(jw) || !cJSON_IsString(js)) ok = false;

    if (ok && !aethernet_pov_token_set_witness(out_token, jw->valuestring)) ok = false;
    if (ok && !aethernet_pov_token_set_subject(out_token, js->valuestring)) ok = false;

    if (ok) {
        // Parse ticks from the raw text with full i64 precision (cJSON's double would
        // truncate a ~6.4e17 tick value). Falls back to the parsed number for the
        // unusual case where the value is not locatable in the source text.
        int64_t ticks = 0;
        if (extract_i64_from_json(json, "timestamp_ticks", &ticks)) {
            out_token->timestamp_ticks = ticks;
        } else if (cJSON_IsNumber(jt)) {
            out_token->timestamp_ticks = (int64_t)jt->valuedouble;
        }
    }
    if (ok && cJSON_IsNumber(jtr)) {
        out_token->transport_used = (aethernet_pov_transport_t)(int)jtr->valuedouble;
    }
    if (ok && jws) parse_sig_hex(jws, out_token->witness_signature, &out_token->witness_signature_len);
    if (ok && jss) parse_sig_hex(jss, out_token->subject_signature, &out_token->subject_signature_len);

    cJSON_Delete(obj);
    if (!ok) aethernet_pov_token_free_fields(out_token);
    return ok;
}

// ── token store (per-subject witness records) ───────────────────────────────
//
// A minimal record of accepted tokens keyed by subject UHID, used only to compute
// the local PoV score (unique-witness count). Anti-Sybil routing/identity signal
// only — NO value semantics.

typedef struct pov_record {
    char *subject_uhid;
    char *witness_uhid;
    struct pov_record *next;
} pov_record_t;

struct aethernet_pov_token_store {
    pov_record_t *head;
};

static aethernet_pov_token_store_t *store_new(void) {
    aethernet_pov_token_store_t *s = (aethernet_pov_token_store_t *)calloc(1, sizeof(*s));
    return s;
}

static void store_free(aethernet_pov_token_store_t *s) {
    if (!s) return;
    pov_record_t *r = s->head;
    while (r) {
        pov_record_t *next = r->next;
        free(r->subject_uhid);
        free(r->witness_uhid);
        free(r);
        r = next;
    }
    free(s);
}

static bool store_record(aethernet_pov_token_store_t *s, const char *subject, const char *witness) {
    if (!s) return false;
    pov_record_t *r = (pov_record_t *)calloc(1, sizeof(*r));
    if (!r) return false;
    r->subject_uhid = dup_str(subject);
    r->witness_uhid = dup_str(witness);
    if (!r->subject_uhid || !r->witness_uhid) {
        free(r->subject_uhid); free(r->witness_uhid); free(r);
        return false;
    }
    r->next = s->head;
    s->head = r;
    return true;
}

// ── PoVTokenExchangeService ─────────────────────────────────────────────────

bool aethernet_pov_exchange_service_init(aethernet_pov_exchange_service_t *svc, void *user_data) {
    if (!svc) return false;
    memset(svc, 0, sizeof(*svc));
    svc->user_data = user_data;
    svc->store = store_new();
    return svc->store != NULL;
}

void aethernet_pov_exchange_service_free_state(aethernet_pov_exchange_service_t *svc) {
    if (!svc) return;
    store_free(svc->store);
    svc->store = NULL;
}

bool aethernet_pov_exchange_service_issue(aethernet_pov_exchange_service_t *svc,
                                          const char *subject_uhid,
                                          aethernet_pov_transport_t transport,
                                          aethernet_pov_token_t *out_token,
                                          bool *out_issued) {
    if (out_issued) *out_issued = false;
    if (!svc || !svc->local_uhid || !svc->identity_sign || !svc->sign_packet || !svc->send) {
        return false;
    }

    // Refusals return success with out_issued = false (no packet sent).
    if (!subject_uhid || subject_uhid[0] == '\0') return true;
    if (!aethernet_pov_transport_is_short_range(transport)) return true; // anti-remote-minting

    const char *local = svc->local_uhid(svc->user_data);
    if (!local || local[0] == '\0') return true;
    if (strcmp(local, subject_uhid) == 0) return true; // a node cannot vouch for itself

    // Caller is responsible for supplying the co-presence timestamp via a wrapper;
    // here we derive it from the current time as .NET ticks. The signed body uses the
    // raw ticks, so the value is whatever the host clock yields at issue time.
    // .NET DateTime.Ticks = Unix-epoch-ticks (621355968000000000) + unix_time * 1e7.
    int64_t timestamp_ticks;
    {
        // Reuse the protocol packet's millisecond clock indirectly: build a throwaway
        // packet to obtain timestamp_ms, then convert. (aethernet_packet_new sets
        // timestamp_ms to now.)
        aethernet_mesh_packet_t *clk = aethernet_packet_new();
        if (!clk) return false;
        int64_t unix_ms = clk->timestamp_ms;
        aethernet_packet_free(clk);
        const int64_t TICKS_PER_MS = 10000LL;
        const int64_t UNIX_EPOCH_TICKS = 621355968000000000LL;
        timestamp_ticks = UNIX_EPOCH_TICKS + unix_ms * TICKS_PER_MS;
    }

    // Build + witness-sign the token.
    aethernet_pov_token_t token;
    aethernet_pov_token_init(&token);
    bool built = aethernet_pov_token_set_witness(&token, local) &&
                 aethernet_pov_token_set_subject(&token, subject_uhid);
    if (!built) { aethernet_pov_token_free_fields(&token); return false; }
    token.timestamp_ticks = timestamp_ticks;
    token.transport_used = transport;

    size_t body_len = 0;
    uint8_t *body = aethernet_pov_token_signable(&token, &body_len);
    if (!body) { aethernet_pov_token_free_fields(&token); return false; }
    bool wsig_ok = svc->identity_sign(svc->user_data, body, body_len, token.witness_signature);
    aethernet_zeroize(body, body_len);
    free(body);
    if (!wsig_ok) { aethernet_pov_token_free_fields(&token); return false; }
    token.witness_signature_len = AETHERNET_POV_SIGNATURE_SIZE;

    // Serialise + wrap in a directed MeshPacket (TTL 1).
    char *json = aethernet_pov_token_to_json(&token);
    if (!json) { aethernet_pov_token_free_fields(&token); return false; }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(json); aethernet_pov_token_free_fields(&token); return false; }
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_POV_TOKEN_EXCHANGE;
    pkt->ttl = 1; // co-present: the subject is one short-range hop away
    bool wrapped = aethernet_packet_set_source_uhid(pkt, local) &&
                   aethernet_packet_set_destination_uhid(pkt, subject_uhid) &&
                   aethernet_packet_set_payload(pkt, (const uint8_t *)json, strlen(json));
    free(json);
    if (!wrapped) { aethernet_packet_free(pkt); aethernet_pov_token_free_fields(&token); return false; }

    if (!svc->sign_packet(svc->user_data, pkt)) {
        aethernet_packet_free(pkt);
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    bool sent_ok = svc->send(svc->user_data, pkt, subject_uhid);
    aethernet_packet_free(pkt);
    if (!sent_ok) { aethernet_pov_token_free_fields(&token); return false; }

    if (out_issued) *out_issued = true;
    if (out_token) {
        *out_token = token; // transfer ownership of heap strings to the caller
    } else {
        aethernet_pov_token_free_fields(&token);
    }
    return true;
}

bool aethernet_pov_exchange_service_handle(aethernet_pov_exchange_service_t *svc,
                                           const aethernet_mesh_packet_t *packet,
                                           const uint8_t *sender_public_key) {
    if (!svc || !packet || !sender_public_key) return false;
    if (!svc->local_uhid || !svc->verify_packet || !svc->identity_sign || !svc->identity_verify) {
        return false;
    }
    if (packet->type != (uint8_t)AETHERNET_PACKET_TYPE_POV_TOKEN_EXCHANGE) return false;

    // 1. Verify the enclosing MeshPacket (also enforces freshness + replay-dedup).
    if (!svc->verify_packet(svc->user_data, packet, sender_public_key)) return false;

    // 2. Deserialise the token body.
    if (!packet->payload || packet->payload_len == 0) return false;
    aethernet_pov_token_t token;
    if (!aethernet_pov_token_from_json((const char *)packet->payload, packet->payload_len, &token)) {
        return false;
    }
    if (!token.witness_uhid || token.witness_uhid[0] == '\0' ||
        !token.subject_uhid || token.subject_uhid[0] == '\0') {
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    // 3. The incoming token must already carry the witness's signature.
    if (token.witness_signature_len == 0) {
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    const char *local = svc->local_uhid(svc->user_data);

    // 4. Ignore our own token echoed back to us (witness == us).
    if (local && local[0] != '\0' && strcmp(token.witness_uhid, local) == 0) {
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    // 5. The token must be addressed to us — we are the subject being vouched for.
    if (local && local[0] != '\0' && strcmp(token.subject_uhid, local) != 0) {
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the
    //    verified sender key (the witness is the packet source).
    size_t body_len = 0;
    uint8_t *body = aethernet_pov_token_signable(&token, &body_len);
    if (!body) { aethernet_pov_token_free_fields(&token); return false; }
    bool wsig_valid = svc->identity_verify(svc->user_data, sender_public_key, body, body_len,
                                           token.witness_signature);
    if (!wsig_valid) {
        aethernet_zeroize(body, body_len);
        free(body);
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    // 6b. A witness must not be vouching for itself.
    if (strcmp(token.witness_uhid, token.subject_uhid) == 0) {
        aethernet_zeroize(body, body_len);
        free(body);
        aethernet_pov_token_free_fields(&token);
        return false;
    }

    // 7. Counter-sign the SAME canonical body as the subject, with our identity key.
    bool ssig_ok = svc->identity_sign(svc->user_data, body, body_len, token.subject_signature);
    aethernet_zeroize(body, body_len);
    free(body);
    if (!ssig_ok) { aethernet_pov_token_free_fields(&token); return false; }
    token.subject_signature_len = AETHERNET_POV_SIGNATURE_SIZE;

    // 8. Record it (increments the witness's contribution to our score) and notify.
    if (!store_record(svc->store, token.subject_uhid, token.witness_uhid)) {
        aethernet_pov_token_free_fields(&token);
        return false;
    }
    if (svc->on_token_received) {
        svc->on_token_received(svc->user_data, &token);
    }

    aethernet_pov_token_free_fields(&token);
    return true;
}

int aethernet_pov_exchange_service_unique_witnesses(const aethernet_pov_exchange_service_t *svc, const char *uhid) {
    if (!svc || !svc->store || !uhid) return 0;

    // Count distinct witness UHIDs among records for this subject. O(n²) over a small
    // local record set — fine for an anti-Sybil signal.
    int unique = 0;
    for (pov_record_t *r = svc->store->head; r; r = r->next) {
        if (strcmp(r->subject_uhid, uhid) != 0) continue;
        bool seen = false;
        for (pov_record_t *q = svc->store->head; q != r; q = q->next) {
            if (strcmp(q->subject_uhid, uhid) == 0 && strcmp(q->witness_uhid, r->witness_uhid) == 0) {
                seen = true;
                break;
            }
        }
        if (!seen) unique++;
    }
    return unique;
}
