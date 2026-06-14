// SPDX-License-Identifier: MIT
//
// Cross-language tipping fixture verifier (C).
//
// Proves the C aethernet_tip_packet implementation reproduces
// fixtures/tipping/tip_packet_basic.json byte-for-byte:
//   - canonical bytes equal the fixture canonical_bytes for every case,
//   - a deterministic Ed25519 sign from the fixture seed reproduces the fixture
//     signature (Ed25519 is deterministic),
//   - the fixture signature verifies against the fixture public key,
//   - a signed payload survives a JSON round-trip with canonical bytes + signature
//     intact,
//   - the MeshTipService send path emits a TipPacket(24) whose payload carries the
//     exact fixture signature, and an inbound tip reaches the settlement hook while a
//     malformed-signature tip is dropped before it.
//
// JSON parsing is a tiny hand-rolled extractor (matching the repo idiom in
// test_signal_fixtures.c / test_bandwidth_fixtures.c) — no JSON library on the test
// surface. Object/array slicing is done by brace/bracket matching; large hex values
// are extracted into freshly-allocated buffers so there is no fixed-size cap.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <direct.h>
#define getcwd _getcwd
#else
#include <unistd.h>
#endif

#include "aethernet/tip_packet.h"
#include "aethernet/security.h"
#include "aethernet/protocol.h"

// ─── Hex helpers ─────────────────────────────────────────────────────────

static int hex_decode_alloc(const char *hex, uint8_t **out, size_t *out_len) {
    size_t n = strlen(hex);
    if (n % 2 != 0) return 0;
    size_t bn = n / 2;
    uint8_t *buf = (uint8_t *)malloc(bn ? bn : 1);
    if (!buf) return 0;
    for (size_t i = 0; i < bn; i++) {
        char b[3] = { hex[i * 2], hex[i * 2 + 1], 0 };
        char *end = NULL;
        unsigned long v = strtoul(b, &end, 16);
        if (end != b + 2) { free(buf); return 0; }
        buf[i] = (uint8_t)v;
    }
    *out = buf;
    *out_len = bn;
    return 1;
}

static void hex_encode(const uint8_t *bytes, size_t len, char *out) {
    static const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = digits[(bytes[i] >> 4) & 0xF];
        out[i * 2 + 1] = digits[bytes[i] & 0xF];
    }
    out[len * 2] = '\0';
}

// ─── File + repo-root helpers ─────────────────────────────────────────────

static char *read_file(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[got] = '\0';
    return buf;
}

static char repo_root_path[1024];

static int find_repo_root(void) {
    char cwd[1024];
    if (!getcwd(cwd, sizeof(cwd))) return 0;
    char path[1280];
    for (int depth = 0; depth < 10; depth++) {
        snprintf(path, sizeof(path), "%s/AetherNetProtocol.slnx", cwd);
        FILE *f = fopen(path, "rb");
        if (f) { fclose(f); strncpy(repo_root_path, cwd, sizeof(repo_root_path) - 1); return 1; }
        char *last_slash = strrchr(cwd, '/');
        if (!last_slash) last_slash = strrchr(cwd, '\\');
        if (!last_slash) return 0;
        *last_slash = '\0';
    }
    return 0;
}

// ─── Tiny JSON extraction ─────────────────────────────────────────────────

// Find "key": then return a pointer just past the colon (skipping whitespace), or
// NULL. Searches only within [scope, scope_end).
static const char *find_key(const char *scope, const char *key) {
    char needle[128];
    snprintf(needle, sizeof(needle), "\"%s\"", key);
    const char *p = strstr(scope, needle);
    if (!p) return NULL;
    p += strlen(needle);
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    if (*p != ':') return NULL;
    p++;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    return p;
}

// Extract a string value for key into a freshly-allocated buffer (no escapes in our
// fixtures except \u sequences which we do not need to decode for hex/ascii fields).
// Returns malloc'd NUL-terminated string, or NULL.
static char *json_str_alloc(const char *scope, const char *key) {
    const char *p = find_key(scope, key);
    if (!p || *p != '\"') return NULL;
    p++; // past opening quote
    const char *start = p;
    while (*p && *p != '\"') {
        if (*p == '\\' && p[1]) p += 2; else p++;
    }
    size_t n = (size_t)(p - start);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, start, n);
    out[n] = '\0';
    return out;
}

// Extract a numeric (integer) value for key. Returns 1 on success.
static int json_i64(const char *scope, const char *key, int64_t *out) {
    const char *p = find_key(scope, key);
    if (!p) return 0;
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return 0;
    *out = (int64_t)v;
    return 1;
}

// Returns 1 if key's value is the literal null.
static int json_is_null(const char *scope, const char *key) {
    const char *p = find_key(scope, key);
    if (!p) return 0;
    return strncmp(p, "null", 4) == 0;
}

// Returns a malloc'd slice of the i-th brace-delimited object inside the array value
// of `array_key`, or NULL when out of range. Sets *next to one past the slice so the
// caller can iterate.
static char *nth_object(const char *scope, const char *array_key, int index) {
    const char *p = find_key(scope, array_key);
    if (!p || *p != '[') return NULL;
    p++; // into array
    int cur = 0;
    while (*p) {
        while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r' || *p == ',') p++;
        if (*p == ']' || *p == '\0') return NULL;
        if (*p != '{') return NULL;
        const char *brace = p;
        int depth = 0;
        const char *end = brace;
        for (; *end; end++) {
            if (*end == '{') depth++;
            else if (*end == '}') { depth--; if (depth == 0) { end++; break; } }
        }
        if (cur == index) {
            size_t n = (size_t)(end - brace);
            char *out = (char *)malloc(n + 1);
            if (!out) return NULL;
            memcpy(out, brace, n);
            out[n] = '\0';
            return out;
        }
        cur++;
        p = end;
    }
    return NULL;
}

// ─── Globals loaded from the fixture ──────────────────────────────────────

static char *g_fixture = NULL;
static uint8_t g_seed[32];
static uint8_t g_pubkey[32];

static int load_fixture(void) {
    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/tipping/tip_packet_basic.json", repo_root_path);
    g_fixture = read_file(path);
    if (!g_fixture) { fprintf(stderr, "  cannot read %s\n", path); return 0; }

    char *seed_hex = json_str_alloc(g_fixture, "ed25519_seed");
    char *pub_hex  = json_str_alloc(g_fixture, "public_key");
    if (!seed_hex || !pub_hex) { free(seed_hex); free(pub_hex); return 0; }

    uint8_t *sb = NULL, *pb = NULL; size_t sn = 0, pn = 0;
    int ok = hex_decode_alloc(seed_hex, &sb, &sn) && sn == 32 &&
             hex_decode_alloc(pub_hex, &pb, &pn) && pn == 32;
    if (ok) { memcpy(g_seed, sb, 32); memcpy(g_pubkey, pb, 32); }
    free(seed_hex); free(pub_hex); free(sb); free(pb);
    return ok;
}

// Populate a tip from the i-th case object. Returns the case slice (caller frees) or
// NULL when out of range.
static char *build_tip_from_case(int i, aethernet_tip_packet_t *tip) {
    char *c = nth_object(g_fixture, "cases", i);
    if (!c) return NULL;

    aethernet_tip_packet_init(tip);
    char *tipper    = json_str_alloc(c, "tipper_uhid");
    char *recipient = json_str_alloc(c, "recipient_uhid");
    char *amount    = json_str_alloc(c, "amount");
    char *traffic   = json_str_alloc(c, "traffic_type");
    int64_t ts = 0;
    json_i64(c, "timestamp_unix_ms", &ts);

    aethernet_tip_packet_set_tipper(tip, tipper ? tipper : "");
    aethernet_tip_packet_set_recipient(tip, recipient ? recipient : "");
    aethernet_tip_packet_set_amount(tip, amount ? amount : "");
    aethernet_tip_packet_set_traffic_type(tip, traffic ? traffic : "");
    tip->timestamp_unix_ms = ts;

    if (!json_is_null(c, "reference_id")) {
        char *ref = json_str_alloc(c, "reference_id");
        if (ref) { aethernet_tip_packet_set_reference_id_guid(tip, ref); free(ref); }
    }

    free(tipper); free(recipient); free(amount); free(traffic);
    return c;
}

// ─── Tests ────────────────────────────────────────────────────────────────

static int test_canonical_bytes(void) {
    printf("Test: tip_canonical_bytes_parity\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        aethernet_tip_packet_t tip;
        char *c = build_tip_from_case(i, &tip);
        if (!c) break;
        n++;

        char *want = json_str_alloc(c, "canonical_bytes");
        size_t len = 0;
        uint8_t *canon = aethernet_tip_packet_build_canonical(&tip, &len);
        char *got = (char *)malloc(len * 2 + 1);
        hex_encode(canon, len, got);

        if (!want || strcmp(got, want) != 0) {
            fprintf(stderr, "  FAIL case %d\n    want: %s\n    got:  %s\n", i, want ? want : "(null)", got);
            ok = 0;
        }
        free(want); free(canon); free(got);
        aethernet_tip_packet_free_fields(&tip);
        free(c);
    }
    if (n == 0) { fprintf(stderr, "  no cases found\n"); ok = 0; }
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

static int test_signature_deterministic(void) {
    printf("Test: tip_signature_deterministic_parity\n");

    // Derive the public key from the seed and confirm it matches the fixture's.
    uint8_t derived_priv[64], derived_pub[32];
    (void)derived_priv;
    // aethernet_ed25519_sign takes the 32-byte seed as the private key; to derive the
    // public key we sign+verify against the fixture pubkey rather than recompute it.
    // (The fixture publishes the pubkey; we verify signatures against it below.)

    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        aethernet_tip_packet_t tip;
        char *c = build_tip_from_case(i, &tip);
        if (!c) break;
        n++;

        char *want_sig = json_str_alloc(c, "signature");

        // Deterministic re-sign from the seed reproduces the exact fixture signature.
        if (!aethernet_tip_packet_sign(&tip, g_seed)) {
            fprintf(stderr, "  FAIL case %d: sign failed\n", i);
            ok = 0;
        } else {
            char got[AETHERNET_TIP_SIGNATURE_SIZE * 2 + 1];
            hex_encode(tip.signature, AETHERNET_TIP_SIGNATURE_SIZE, got);
            if (!want_sig || strcmp(got, want_sig) != 0) {
                fprintf(stderr, "  FAIL case %d signature\n    want: %s\n    got:  %s\n",
                        i, want_sig ? want_sig : "(null)", got);
                ok = 0;
            }
            // The fixture signature verifies against the fixture public key.
            if (!aethernet_tip_packet_verify(&tip, g_pubkey)) {
                fprintf(stderr, "  FAIL case %d: signature did not verify\n", i);
                ok = 0;
            }
        }

        free(want_sig);
        aethernet_tip_packet_free_fields(&tip);
        free(c);
    }
    (void)derived_pub;
    if (n == 0) { fprintf(stderr, "  no cases found\n"); ok = 0; }
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

static int test_json_round_trip(void) {
    printf("Test: tip_json_round_trip\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        aethernet_tip_packet_t tip;
        char *c = build_tip_from_case(i, &tip);
        if (!c) break;
        n++;

        aethernet_tip_packet_sign(&tip, g_seed);

        size_t before_len = 0;
        uint8_t *before = aethernet_tip_packet_build_canonical(&tip, &before_len);

        char *json = aethernet_tip_packet_to_json(&tip);
        aethernet_tip_packet_t back;
        int parsed = json && aethernet_tip_packet_from_json(json, strlen(json), &back);

        if (!parsed) {
            fprintf(stderr, "  FAIL case %d: round-trip parse failed\n", i);
            ok = 0;
        } else {
            size_t after_len = 0;
            uint8_t *after = aethernet_tip_packet_build_canonical(&back, &after_len);
            if (after_len != before_len || memcmp(before, after, before_len) != 0) {
                fprintf(stderr, "  FAIL case %d: canonical bytes changed across round-trip\n", i);
                ok = 0;
            }
            if (back.signature_len != tip.signature_len ||
                memcmp(back.signature, tip.signature, tip.signature_len) != 0) {
                fprintf(stderr, "  FAIL case %d: signature changed across round-trip\n", i);
                ok = 0;
            }
            if (back.has_reference_id != tip.has_reference_id) {
                fprintf(stderr, "  FAIL case %d: reference_id nullity changed\n", i);
                ok = 0;
            }
            free(after);
            aethernet_tip_packet_free_fields(&back);
        }

        free(before); free(json);
        aethernet_tip_packet_free_fields(&tip);
        free(c);
    }
    if (n == 0) ok = 0;
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

// ─── MeshTipService dispatch doubles ──────────────────────────────────────

typedef struct {
    const char *local;
    int sent_count;
    int broadcast_count;
    int settle_count;
    char last_settle_tipper[256];
} tip_test_ctx;

static const char *ctx_local(void *u) { return ((tip_test_ctx *)u)->local; }

static bool ctx_identity_sign(void *u, const uint8_t *data, size_t len, uint8_t *out) {
    (void)u;
    return aethernet_ed25519_sign(g_seed, data, len, out);
}

static bool ctx_sign_packet(void *u, aethernet_mesh_packet_t *pkt) {
    (void)u;
    // Stamp a fake envelope signature + nonce (the body signature is what we assert).
    uint8_t sig[8] = { 'e','n','v','-','s','i','g','!' };
    aethernet_packet_set_signature(pkt, sig, sizeof(sig));
    return true;
}

static bool ctx_send(void *u, const aethernet_mesh_packet_t *pkt, const char *next) {
    (void)pkt; (void)next;
    ((tip_test_ctx *)u)->sent_count++;
    return true;
}

static int ctx_broadcast(void *u, const aethernet_mesh_packet_t *pkt) {
    (void)pkt;
    ((tip_test_ctx *)u)->broadcast_count++;
    return 1;
}

static int ctx_settle(void *u, const aethernet_tip_packet_t *payload) {
    tip_test_ctx *c = (tip_test_ctx *)u;
    c->settle_count++;
    snprintf(c->last_settle_tipper, sizeof(c->last_settle_tipper), "%s",
             payload->tipper_uhid ? payload->tipper_uhid : "");
    return 0;
}

static int test_service_send_and_handle(void) {
    printf("Test: tip_service_send_and_handle\n");
    int ok = 1;

    char *c0 = nth_object(g_fixture, "cases", 0);
    if (!c0) { fprintf(stderr, "  no case 0\n"); return 0; }
    char *tipper    = json_str_alloc(c0, "tipper_uhid");
    char *recipient = json_str_alloc(c0, "recipient_uhid");
    char *amount    = json_str_alloc(c0, "amount");
    char *traffic   = json_str_alloc(c0, "traffic_type");
    char *want_sig  = json_str_alloc(c0, "signature");
    char *ref       = json_str_alloc(c0, "reference_id");
    int64_t ts = 0; json_i64(c0, "timestamp_unix_ms", &ts);

    // Reference id bytes (.NET order) for the send path.
    aethernet_tip_packet_t tmp; aethernet_tip_packet_init(&tmp);
    uint8_t ref_bytes[16]; bool have_ref = false;
    if (ref && aethernet_tip_packet_set_reference_id_guid(&tmp, ref)) {
        memcpy(ref_bytes, tmp.reference_id, 16);
        have_ref = true;
    }
    aethernet_tip_packet_free_fields(&tmp);

    // ── send path: emitted TipPacket(24) carries the exact fixture signature ──
    tip_test_ctx sctx;
    memset(&sctx, 0, sizeof(sctx));
    sctx.local = tipper;

    aethernet_mesh_tip_service_t svc;
    aethernet_mesh_tip_service_init(&svc, &sctx);
    svc.local_uhid = ctx_local;
    svc.identity_sign = ctx_identity_sign;
    svc.sign_packet = ctx_sign_packet;
    svc.send = ctx_send;
    svc.broadcast = ctx_broadcast;
    svc.find_next_hop = NULL;   // force broadcast
    svc.settle = NULL;

    aethernet_mesh_packet_t *emitted = NULL;
    bool sent = aethernet_mesh_tip_service_send(&svc, recipient, amount, traffic,
                                                have_ref ? ref_bytes : NULL, ts, &emitted);
    if (!sent || !emitted) {
        fprintf(stderr, "  FAIL: send returned false\n");
        ok = 0;
    } else {
        if (emitted->type != (uint8_t)AETHERNET_PACKET_TYPE_TIP_PACKET) {
            fprintf(stderr, "  FAIL: emitted packet type %d != TipPacket(24)\n", emitted->type);
            ok = 0;
        }
        // Parse the emitted body and check the payload signature == fixture signature.
        aethernet_tip_packet_t got;
        if (aethernet_tip_packet_from_json((const char *)emitted->payload, emitted->payload_len, &got)) {
            char got_sig[AETHERNET_TIP_SIGNATURE_SIZE * 2 + 1];
            hex_encode(got.signature, AETHERNET_TIP_SIGNATURE_SIZE, got_sig);
            if (!want_sig || strcmp(got_sig, want_sig) != 0) {
                fprintf(stderr, "  FAIL: service signature\n    want: %s\n    got:  %s\n",
                        want_sig ? want_sig : "(null)", got_sig);
                ok = 0;
            }
            aethernet_tip_packet_free_fields(&got);
        } else {
            fprintf(stderr, "  FAIL: could not parse emitted payload\n");
            ok = 0;
        }
        if (sctx.broadcast_count != 1 || sctx.sent_count != 0) {
            fprintf(stderr, "  FAIL: expected 1 broadcast/0 unicast, got %d/%d\n",
                    sctx.broadcast_count, sctx.sent_count);
            ok = 0;
        }
    }
    if (emitted) aethernet_packet_free(emitted);

    // ── receive path: settlement hook fires; malformed-sig tip is dropped ──
    tip_test_ctx rctx;
    memset(&rctx, 0, sizeof(rctx));
    rctx.local = recipient; // we are the addressed recipient -> no onward relay

    aethernet_mesh_tip_service_t rsvc;
    aethernet_mesh_tip_service_init(&rsvc, &rctx);
    rsvc.local_uhid = ctx_local;
    rsvc.identity_sign = ctx_identity_sign;
    rsvc.sign_packet = ctx_sign_packet;
    rsvc.send = ctx_send;
    rsvc.broadcast = ctx_broadcast;
    rsvc.find_next_hop = NULL;
    rsvc.settle = ctx_settle;

    // Build a well-formed, signed inbound tip.
    aethernet_tip_packet_t in; aethernet_tip_packet_init(&in);
    aethernet_tip_packet_set_tipper(&in, tipper);
    aethernet_tip_packet_set_recipient(&in, recipient);
    aethernet_tip_packet_set_amount(&in, amount);
    aethernet_tip_packet_set_traffic_type(&in, traffic);
    in.timestamp_unix_ms = ts;
    if (have_ref) aethernet_tip_packet_set_reference_id(&in, ref_bytes);
    aethernet_tip_packet_sign(&in, g_seed);
    char *in_json = aethernet_tip_packet_to_json(&in);

    aethernet_mesh_packet_t *inpkt = aethernet_packet_new();
    inpkt->type = (uint8_t)AETHERNET_PACKET_TYPE_TIP_PACKET;
    aethernet_packet_set_source_uhid(inpkt, tipper);
    aethernet_packet_set_destination_uhid(inpkt, recipient);
    aethernet_packet_set_payload(inpkt, (const uint8_t *)in_json, strlen(in_json));

    bool handled = aethernet_mesh_tip_service_handle(&rsvc, inpkt);
    if (!handled || rctx.settle_count != 1 ||
        strcmp(rctx.last_settle_tipper, tipper) != 0) {
        fprintf(stderr, "  FAIL: settlement hook did not fire as expected (handled=%d count=%d)\n",
                handled, rctx.settle_count);
        ok = 0;
    }

    // Malformed signature -> dropped before the hook.
    rctx.settle_count = 0;
    in.signature_len = 3; // truncate to a non-64 length
    char *bad_json = aethernet_tip_packet_to_json(&in); // signature omitted (len != 64)
    aethernet_mesh_packet_t *badpkt = aethernet_packet_new();
    badpkt->type = (uint8_t)AETHERNET_PACKET_TYPE_TIP_PACKET;
    aethernet_packet_set_source_uhid(badpkt, tipper);
    aethernet_packet_set_destination_uhid(badpkt, recipient);
    aethernet_packet_set_payload(badpkt, (const uint8_t *)bad_json, strlen(bad_json));

    bool bad_handled = aethernet_mesh_tip_service_handle(&rsvc, badpkt);
    if (bad_handled || rctx.settle_count != 0) {
        fprintf(stderr, "  FAIL: malformed-signature tip was not dropped (handled=%d count=%d)\n",
                bad_handled, rctx.settle_count);
        ok = 0;
    }

    aethernet_packet_free(inpkt);
    aethernet_packet_free(badpkt);
    aethernet_tip_packet_free_fields(&in);
    free(in_json); free(bad_json);
    free(tipper); free(recipient); free(amount); free(traffic); free(want_sig); free(ref);
    free(c0);

    if (ok) printf("  PASS\n");
    return ok;
}

int main(void) {
    if (!find_repo_root()) {
        fprintf(stderr, "Cannot locate repo root (looking for AetherNetProtocol.slnx).\n");
        return 1;
    }
    printf("Tipping fixture verifier — repo root: %s\n", repo_root_path);
    if (!load_fixture()) {
        fprintf(stderr, "Cannot load tipping fixture.\n");
        return 1;
    }

    int total = 0, passed = 0;
    total++; if (test_canonical_bytes()) passed++;
    total++; if (test_signature_deterministic()) passed++;
    total++; if (test_json_round_trip()) passed++;
    total++; if (test_service_send_and_handle()) passed++;

    free(g_fixture);
    printf("\n%d/%d tests passed.\n", passed, total);
    return passed == total ? 0 : 1;
}
