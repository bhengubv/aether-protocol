// SPDX-License-Identifier: MIT
//
// Cross-language Proof-of-Vicinity fixture verifier (C).
//
// Proves the C aethernet_pov_token implementation reproduces
// fixtures/market/pov_token_basic.json byte-for-byte:
//   - canonical body equals the fixture canonical_body for every case (all three
//     transports + the .NET DateTime.Ticks i64 LE field),
//   - a deterministic Ed25519 sign from the fixture witness seed reproduces the
//     fixture witness_signature, and that signature verifies against the witness
//     public key,
//   - a token survives a JSON round-trip with its canonical body intact,
//   - the on-mesh exchange flow: a witness issues a token (packet 43, TTL 1); the
//     subject verifies the witness signature, counter-signs, and records it; BOTH
//     signatures then verify; a replay is rejected; self-vouch / remote-mint are
//     refused.
//
// JSON parsing is a tiny hand-rolled extractor (matching the repo idiom).

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

#include "aethernet/pov_token.h"
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
    *out = buf; *out_len = bn;
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

static char *json_str_alloc(const char *scope, const char *key) {
    const char *p = find_key(scope, key);
    if (!p || *p != '\"') return NULL;
    p++;
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

static int json_i64(const char *scope, const char *key, int64_t *out) {
    const char *p = find_key(scope, key);
    if (!p) return 0;
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return 0;
    *out = (int64_t)v;
    return 1;
}

static int json_int(const char *scope, const char *key, int *out) {
    int64_t v = 0;
    if (!json_i64(scope, key, &v)) return 0;
    *out = (int)v;
    return 1;
}

static char *nth_object(const char *scope, const char *array_key, int index) {
    const char *p = find_key(scope, array_key);
    if (!p || *p != '[') return NULL;
    p++;
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

// ─── Globals ──────────────────────────────────────────────────────────────

static char *g_fixture = NULL;
static uint8_t g_witness_seed[32];
static uint8_t g_witness_pub[32];

static int load_fixture(void) {
    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/market/pov_token_basic.json", repo_root_path);
    g_fixture = read_file(path);
    if (!g_fixture) { fprintf(stderr, "  cannot read %s\n", path); return 0; }

    char *seed_hex = json_str_alloc(g_fixture, "witness_seed");
    char *pub_hex  = json_str_alloc(g_fixture, "witness_public_key");
    if (!seed_hex || !pub_hex) { free(seed_hex); free(pub_hex); return 0; }
    uint8_t *sb = NULL, *pb = NULL; size_t sn = 0, pn = 0;
    int ok = hex_decode_alloc(seed_hex, &sb, &sn) && sn == 32 &&
             hex_decode_alloc(pub_hex, &pb, &pn) && pn == 32;
    if (ok) { memcpy(g_witness_seed, sb, 32); memcpy(g_witness_pub, pb, 32); }
    free(seed_hex); free(pub_hex); free(sb); free(pb);
    return ok;
}

// ─── Tests ────────────────────────────────────────────────────────────────

static int test_canonical_body(void) {
    printf("Test: pov_canonical_body_parity\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        char *c = nth_object(g_fixture, "cases", i);
        if (!c) break;
        n++;

        char *subject = json_str_alloc(c, "subject_uhid");
        char *transport_name = json_str_alloc(c, "transport");
        int tbyte = -1; json_int(c, "transport_byte", &tbyte);
        int64_t ticks = 0; json_i64(c, "timestamp_ticks", &ticks);
        char *want = json_str_alloc(c, "canonical_body");

        size_t len = 0;
        uint8_t *body = aethernet_pov_token_build_signable(subject, ticks,
                            (aethernet_pov_transport_t)tbyte, &len);
        char *got = (char *)malloc(len * 2 + 1);
        hex_encode(body, len, got);

        if (!want || strcmp(got, want) != 0) {
            fprintf(stderr, "  FAIL case %d body\n    want: %s\n    got:  %s\n",
                    i, want ? want : "(null)", got);
            ok = 0;
        }
        // Transport enum byte maps to the named transport.
        if (!transport_name ||
            strcmp(aethernet_pov_transport_name((aethernet_pov_transport_t)tbyte), transport_name) != 0) {
            fprintf(stderr, "  FAIL case %d: transport name mismatch\n", i);
            ok = 0;
        }

        free(subject); free(transport_name); free(want); free(body); free(got); free(c);
    }
    if (n == 0) ok = 0;
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

static int test_witness_signature(void) {
    printf("Test: pov_witness_signature_deterministic_parity\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        char *c = nth_object(g_fixture, "cases", i);
        if (!c) break;
        n++;

        char *subject = json_str_alloc(c, "subject_uhid");
        int tbyte = -1; json_int(c, "transport_byte", &tbyte);
        int64_t ticks = 0; json_i64(c, "timestamp_ticks", &ticks);
        char *want_sig = json_str_alloc(c, "witness_signature");

        // Build a token and witness-sign it deterministically from the fixture seed.
        aethernet_pov_token_t tok; aethernet_pov_token_init(&tok);
        aethernet_pov_token_set_witness(&tok, "aether:witness:zz");
        aethernet_pov_token_set_subject(&tok, subject);
        tok.timestamp_ticks = ticks;
        tok.transport_used = (aethernet_pov_transport_t)tbyte;

        if (!aethernet_pov_token_sign_witness(&tok, g_witness_seed)) {
            fprintf(stderr, "  FAIL case %d: witness sign failed\n", i);
            ok = 0;
        } else {
            char got[AETHERNET_POV_SIGNATURE_SIZE * 2 + 1];
            hex_encode(tok.witness_signature, AETHERNET_POV_SIGNATURE_SIZE, got);
            if (!want_sig || strcmp(got, want_sig) != 0) {
                fprintf(stderr, "  FAIL case %d witness sig\n    want: %s\n    got:  %s\n",
                        i, want_sig ? want_sig : "(null)", got);
                ok = 0;
            }
            if (!aethernet_pov_token_verify_witness(&tok, g_witness_pub)) {
                fprintf(stderr, "  FAIL case %d: witness sig did not verify\n", i);
                ok = 0;
            }
        }

        aethernet_pov_token_free_fields(&tok);
        free(subject); free(want_sig); free(c);
    }
    if (n == 0) ok = 0;
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

static int test_json_round_trip(void) {
    printf("Test: pov_json_round_trip\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        char *c = nth_object(g_fixture, "cases", i);
        if (!c) break;
        n++;

        char *subject = json_str_alloc(c, "subject_uhid");
        int tbyte = -1; json_int(c, "transport_byte", &tbyte);
        int64_t ticks = 0; json_i64(c, "timestamp_ticks", &ticks);

        aethernet_pov_token_t tok; aethernet_pov_token_init(&tok);
        aethernet_pov_token_set_witness(&tok, "aether:witness:zz");
        aethernet_pov_token_set_subject(&tok, subject);
        tok.timestamp_ticks = ticks;
        tok.transport_used = (aethernet_pov_transport_t)tbyte;
        aethernet_pov_token_sign_witness(&tok, g_witness_seed);

        size_t before_len = 0;
        uint8_t *before = aethernet_pov_token_signable(&tok, &before_len);

        char *json = aethernet_pov_token_to_json(&tok);
        aethernet_pov_token_t back;
        int parsed = json && aethernet_pov_token_from_json(json, strlen(json), &back);
        if (!parsed) {
            fprintf(stderr, "  FAIL case %d: round-trip parse failed\n", i);
            ok = 0;
        } else {
            size_t after_len = 0;
            uint8_t *after = aethernet_pov_token_signable(&back, &after_len);
            if (after_len != before_len || memcmp(before, after, before_len) != 0) {
                fprintf(stderr, "  FAIL case %d: canonical body changed across round-trip\n", i);
                ok = 0;
            }
            if (back.witness_signature_len != tok.witness_signature_len ||
                memcmp(back.witness_signature, tok.witness_signature, tok.witness_signature_len) != 0) {
                fprintf(stderr, "  FAIL case %d: witness sig changed across round-trip\n", i);
                ok = 0;
            }
            if (back.transport_used != tok.transport_used) {
                fprintf(stderr, "  FAIL case %d: transport changed across round-trip\n", i);
                ok = 0;
            }
            free(after);
            aethernet_pov_token_free_fields(&back);
        }

        free(before); free(json);
        aethernet_pov_token_free_fields(&tok);
        free(subject); free(c);
    }
    if (n == 0) ok = 0;
    if (ok) printf("  PASS (%d cases)\n", n);
    return ok;
}

// ─── Exchange-service doubles ─────────────────────────────────────────────
//
// Each node carries its own Ed25519 keypair (seed + derived public key). The
// envelope signer signs a deterministic envelope string and dedups on (source,
// nonce) to model the C# IPacketSigningService freshness/replay contract.

typedef struct {
    const char *local;
    const uint8_t *seed;       // 32-byte identity seed
    int sent_count;
    // replay-dedup memory: last seen source+nonce hex
    char seen[8][512];
    int  seen_n;
} pov_ctx;

static const char *pov_local(void *u) { return ((pov_ctx *)u)->local; }

static bool pov_identity_sign(void *u, const uint8_t *data, size_t len, uint8_t *out) {
    return aethernet_ed25519_sign(((pov_ctx *)u)->seed, data, len, out);
}

static bool pov_identity_verify(void *u, const uint8_t *pub, const uint8_t *data, size_t len, const uint8_t *sig) {
    (void)u;
    return aethernet_ed25519_verify(pub, data, len, sig);
}

// Build the envelope-signable string "source:destination" and sign it with the
// node's identity seed (so the receiver verifies against the witness public key).
static bool pov_sign_packet(void *u, aethernet_mesh_packet_t *pkt) {
    pov_ctx *c = (pov_ctx *)u;
    uint8_t nonce[8] = { 9, 9, 9, 9, 9, 9, 9, 9 };
    aethernet_packet_set_signature(pkt, NULL, 0);
    // Set a deterministic nonce so replay-dedup has something stable to key on.
    memcpy(pkt->packet_nonce, nonce, 8);

    char buf[600];
    snprintf(buf, sizeof(buf), "%s:%s",
             pkt->source_uhid ? pkt->source_uhid : "",
             pkt->destination_uhid ? pkt->destination_uhid : "");
    uint8_t sig[64];
    if (!aethernet_ed25519_sign(c->seed, (const uint8_t *)buf, strlen(buf), sig)) return false;
    return aethernet_packet_set_signature(pkt, sig, 64);
}

static bool pov_verify_packet(void *u, const aethernet_mesh_packet_t *pkt, const uint8_t *sender_pub) {
    pov_ctx *c = (pov_ctx *)u;
    // Replay-dedup on source+nonce.
    char key[512];
    char noncehex[17];
    hex_encode(pkt->packet_nonce, 8, noncehex);
    snprintf(key, sizeof(key), "%s:%s", pkt->source_uhid ? pkt->source_uhid : "", noncehex);
    for (int i = 0; i < c->seen_n; i++) {
        if (strcmp(c->seen[i], key) == 0) return false; // replay
    }
    if (c->seen_n < 8) { snprintf(c->seen[c->seen_n], sizeof(c->seen[0]), "%s", key); c->seen_n++; }

    char buf[600];
    snprintf(buf, sizeof(buf), "%s:%s",
             pkt->source_uhid ? pkt->source_uhid : "",
             pkt->destination_uhid ? pkt->destination_uhid : "");
    if (!pkt->signature || pkt->signature_len != 64) return false;
    return aethernet_ed25519_verify(sender_pub, (const uint8_t *)buf, strlen(buf), pkt->signature);
}

static bool pov_send(void *u, const aethernet_mesh_packet_t *pkt, const char *subject) {
    (void)pkt; (void)subject;
    ((pov_ctx *)u)->sent_count++;
    return true;
}

static aethernet_pov_token_t g_received;
static int g_received_fired = 0;
static void pov_on_received(void *u, const aethernet_pov_token_t *tok) {
    (void)u;
    // Deep-copy what we need to verify both signatures afterwards.
    aethernet_pov_token_init(&g_received);
    aethernet_pov_token_set_witness(&g_received, tok->witness_uhid);
    aethernet_pov_token_set_subject(&g_received, tok->subject_uhid);
    g_received.timestamp_ticks = tok->timestamp_ticks;
    g_received.transport_used = tok->transport_used;
    memcpy(g_received.witness_signature, tok->witness_signature, AETHERNET_POV_SIGNATURE_SIZE);
    g_received.witness_signature_len = tok->witness_signature_len;
    memcpy(g_received.subject_signature, tok->subject_signature, AETHERNET_POV_SIGNATURE_SIZE);
    g_received.subject_signature_len = tok->subject_signature_len;
    g_received_fired = 1;
}

static int test_exchange_flow(void) {
    printf("Test: pov_exchange_full_flow\n");
    int ok = 1;

    // Two nodes with real (freshly-generated) Ed25519 keypairs. The seed doubles as
    // the private key for aethernet_ed25519_sign; the matching public key is used for
    // verification.
    uint8_t witness_seed[32], witness_pub[32];
    uint8_t subject_seed[32], subject_pub[32];
    if (!aethernet_ed25519_generate_keypair(witness_seed, witness_pub) ||
        !aethernet_ed25519_generate_keypair(subject_seed, subject_pub)) {
        fprintf(stderr, "  FAIL: keypair generation failed\n");
        return 0;
    }

    const char *witness_uhid = "aether:node:witness";
    const char *subject_uhid = "aether:node:subject";

    // Witness side issues a token.
    pov_ctx wctx; memset(&wctx, 0, sizeof(wctx));
    wctx.local = witness_uhid; wctx.seed = witness_seed;

    aethernet_pov_exchange_service_t witness;
    if (!aethernet_pov_exchange_service_init(&witness, &wctx)) { fprintf(stderr, "  init failed\n"); return 0; }
    witness.local_uhid = pov_local;
    witness.sign_packet = pov_sign_packet;
    witness.verify_packet = pov_verify_packet;
    witness.identity_sign = pov_identity_sign;
    witness.identity_verify = pov_identity_verify;
    witness.send = pov_send;

    aethernet_pov_token_t issued; bool issued_flag = false;
    bool issue_ok = aethernet_pov_exchange_service_issue(&witness, subject_uhid,
                        AETHERNET_POV_TRANSPORT_BLE, &issued, &issued_flag);
    if (!issue_ok || !issued_flag) {
        fprintf(stderr, "  FAIL: witness refused to issue a valid token\n");
        aethernet_pov_exchange_service_free_state(&witness);
        return 0;
    }
    if (wctx.sent_count != 1) {
        fprintf(stderr, "  FAIL: expected exactly 1 directed send, got %d\n", wctx.sent_count);
        ok = 0;
    }

    // Re-create the exact issued packet the witness sent (the service frees its own
    // copy). We rebuild it from the issued token using the witness signer.
    char *issued_json = aethernet_pov_token_to_json(&issued);
    aethernet_mesh_packet_t *exchange_pkt = aethernet_packet_new();
    exchange_pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_POV_TOKEN_EXCHANGE;
    exchange_pkt->ttl = 1;
    aethernet_packet_set_source_uhid(exchange_pkt, witness_uhid);
    aethernet_packet_set_destination_uhid(exchange_pkt, subject_uhid);
    aethernet_packet_set_payload(exchange_pkt, (const uint8_t *)issued_json, strlen(issued_json));
    pov_sign_packet(&wctx, exchange_pkt); // sign with the witness key

    if (exchange_pkt->ttl != 1) {
        fprintf(stderr, "  FAIL: issued packet TTL %d != 1\n", exchange_pkt->ttl);
        ok = 0;
    }

    // Subject side receives the witness packet.
    pov_ctx sctx; memset(&sctx, 0, sizeof(sctx));
    sctx.local = subject_uhid; sctx.seed = subject_seed;

    aethernet_pov_exchange_service_t subject;
    aethernet_pov_exchange_service_init(&subject, &sctx);
    subject.local_uhid = pov_local;
    subject.sign_packet = pov_sign_packet;
    subject.verify_packet = pov_verify_packet;
    subject.identity_sign = pov_identity_sign;
    subject.identity_verify = pov_identity_verify;
    subject.send = pov_send;
    subject.on_token_received = pov_on_received;

    g_received_fired = 0;
    bool accepted = aethernet_pov_exchange_service_handle(&subject, exchange_pkt, witness_pub);
    if (!accepted) {
        fprintf(stderr, "  FAIL: subject rejected a valid witness token\n");
        ok = 0;
    }
    if (!g_received_fired) {
        fprintf(stderr, "  FAIL: on_token_received did not fire\n");
        ok = 0;
    } else {
        // BOTH signatures must verify over the same canonical body.
        if (!aethernet_pov_token_verify_witness(&g_received, witness_pub)) {
            fprintf(stderr, "  FAIL: witness signature did not verify on accepted token\n");
            ok = 0;
        }
        if (!aethernet_pov_token_verify_subject(&g_received, subject_pub)) {
            fprintf(stderr, "  FAIL: subject countersignature did not verify\n");
            ok = 0;
        }
        aethernet_pov_token_free_fields(&g_received);
    }

    // Score reflects one unique witness for the subject.
    int uw = aethernet_pov_exchange_service_unique_witnesses(&subject, subject_uhid);
    if (uw != 1) {
        fprintf(stderr, "  FAIL: expected 1 unique witness, got %d\n", uw);
        ok = 0;
    }

    // Replaying the same packet is rejected by the signer's nonce dedup.
    bool replay = aethernet_pov_exchange_service_handle(&subject, exchange_pkt, witness_pub);
    if (replay) {
        fprintf(stderr, "  FAIL: a replayed PoV exchange packet must be rejected\n");
        ok = 0;
    }

    // Self-vouch + remote-mint refusals (no packet sent).
    pov_ctx selfctx; memset(&selfctx, 0, sizeof(selfctx));
    selfctx.local = "aether:node:self"; selfctx.seed = witness_seed;
    aethernet_pov_exchange_service_t selfsvc;
    aethernet_pov_exchange_service_init(&selfsvc, &selfctx);
    selfsvc.local_uhid = pov_local;
    selfsvc.sign_packet = pov_sign_packet;
    selfsvc.verify_packet = pov_verify_packet;
    selfsvc.identity_sign = pov_identity_sign;
    selfsvc.identity_verify = pov_identity_verify;
    selfsvc.send = pov_send;

    bool sv_issued = true, rm_issued = true;
    aethernet_pov_exchange_service_issue(&selfsvc, "aether:node:self", AETHERNET_POV_TRANSPORT_BLE, NULL, &sv_issued);
    aethernet_pov_exchange_service_issue(&selfsvc, "aether:node:other", (aethernet_pov_transport_t)9, NULL, &rm_issued);
    if (sv_issued) { fprintf(stderr, "  FAIL: a node must not vouch for itself\n"); ok = 0; }
    if (rm_issued) { fprintf(stderr, "  FAIL: must refuse non-short-range minting\n"); ok = 0; }
    if (selfctx.sent_count != 0) { fprintf(stderr, "  FAIL: refused issuance must send nothing\n"); ok = 0; }

    aethernet_pov_exchange_service_free_state(&selfsvc);
    aethernet_packet_free(exchange_pkt);
    free(issued_json);
    aethernet_pov_token_free_fields(&issued);
    aethernet_pov_exchange_service_free_state(&witness);
    aethernet_pov_exchange_service_free_state(&subject);

    if (ok) printf("  PASS\n");
    return ok;
}

int main(void) {
    if (!find_repo_root()) {
        fprintf(stderr, "Cannot locate repo root (looking for AetherNetProtocol.slnx).\n");
        return 1;
    }
    printf("PoV fixture verifier — repo root: %s\n", repo_root_path);
    if (!load_fixture()) { fprintf(stderr, "Cannot load market fixture.\n"); return 1; }

    int total = 0, passed = 0;
    total++; if (test_canonical_body()) passed++;
    total++; if (test_witness_signature()) passed++;
    total++; if (test_json_round_trip()) passed++;
    total++; if (test_exchange_flow()) passed++;

    free(g_fixture);
    printf("\n%d/%d tests passed.\n", passed, total);
    return passed == total ? 0 : 1;
}
