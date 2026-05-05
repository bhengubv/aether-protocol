// SPDX-License-Identifier: MIT
//
// Cross-language Signal-protocol fixture verifier (C).
//
// Verifies that the C implementation produces byte-identical X3DH and
// ratchet outputs to the C# reference (committed in
// fixtures/signal/expected/*.json). Any drift between C and the other
// languages surfaces here as a hex mismatch.
//
// JSON parsing here is a tiny hand-rolled extractor — the cases we read
// have flat string-only fields, so we don't need a JSON library.

#include <assert.h>
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

#include "aether/security.h"

// ─── Hex helpers ─────────────────────────────────────────────────────────

static int hex_decode(const char *hex, uint8_t *out, size_t out_len) {
    size_t n = strlen(hex);
    if (n != out_len * 2) return 0;
    for (size_t i = 0; i < out_len; i++) {
        char b[3] = { hex[i * 2], hex[i * 2 + 1], 0 };
        out[i] = (uint8_t)strtoul(b, NULL, 16);
    }
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

// ─── Tiny JSON extractor ──────────────────────────────────────────────────

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

// Locates "key": "value" within `scope` (which can be the full file or a
// case-object slice) and writes the value into `out` (caller-allocated, at
// least `out_size` bytes). Returns 1 on success, 0 if not found.
static int json_get_str(const char *scope, const char *key, char *out, size_t out_size) {
    char needle[128];
    int needle_n = snprintf(needle, sizeof(needle), "\"%s\"", key);
    const char *p = strstr(scope, needle);
    if (!p) return 0;
    p += needle_n;
    while (*p && *p != '\"') p++;
    if (*p != '\"') return 0;
    p++; // past opening quote
    size_t i = 0;
    while (*p && *p != '\"' && i + 1 < out_size) {
        if (*p == '\\' && p[1]) p++; // skip escapes (we don't have any in our values)
        out[i++] = *p++;
    }
    out[i] = '\0';
    return *p == '\"';
}

// Locates the case object whose "name" field equals `case_name`. Returns a
// freshly-allocated copy of just that object's brace-bracketed text.
static char *find_case(const char *json, const char *case_name) {
    const char *p = json;
    while ((p = strstr(p, "\"name\"")) != NULL) {
        p += strlen("\"name\"");
        while (*p && *p != '\"') p++;
        if (*p != '\"') return NULL;
        p++;
        const char *name_start = p;
        while (*p && *p != '\"') p++;
        if ((size_t)(p - name_start) == strlen(case_name) &&
            strncmp(name_start, case_name, strlen(case_name)) == 0) {
            // Walk back to the opening '{', then forward to its matching '}'.
            const char *brace = name_start;
            while (brace > json && *brace != '{') brace--;
            if (*brace != '{') return NULL;
            int depth = 0;
            const char *end = brace;
            for (; *end; end++) {
                if (*end == '{') depth++;
                else if (*end == '}') { depth--; if (depth == 0) { end++; break; } }
            }
            size_t n = (size_t)(end - brace);
            char *out = (char *)malloc(n + 1);
            memcpy(out, brace, n);
            out[n] = '\0';
            return out;
        }
    }
    return NULL;
}

// ─── Repo-root locator ────────────────────────────────────────────────────

static char repo_root_path[1024];

static int find_repo_root(void) {
    char cwd[1024];
    if (!getcwd(cwd, sizeof(cwd))) return 0;
    char path[1280];
    for (int depth = 0; depth < 10; depth++) {
        snprintf(path, sizeof(path), "%s/AetherProtocol.slnx", cwd);
        FILE *f = fopen(path, "rb");
        if (f) { fclose(f); strncpy(repo_root_path, cwd, sizeof(repo_root_path) - 1); return 1; }
        // Walk up one level.
        char *last_slash = strrchr(cwd, '/');
        if (!last_slash) {
            last_slash = strrchr(cwd, '\\');
        }
        if (!last_slash) return 0;
        *last_slash = '\0';
    }
    return 0;
}

static char *load_inputs(void) {
    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/signal/inputs.json", repo_root_path);
    return read_file(path);
}

static char *load_expected(const char *case_name) {
    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/signal/expected/%s.json", repo_root_path, case_name);
    return read_file(path);
}

// ─── HMAC-SHA256(key, single byte) helper ────────────────────────────────

static int hmac_one(const uint8_t *key, size_t key_len, uint8_t b, uint8_t out[32]) {
    uint8_t msg[1] = { b };
    return aether_hmac_sha256(key, key_len, msg, sizeof(msg), out) ? 1 : 0;
}

// ─── Test cases ──────────────────────────────────────────────────────────

static int assert_hex_equal(const char *label, const uint8_t *actual, size_t actual_len, const char *expected_hex) {
    char actual_hex[256];
    hex_encode(actual, actual_len, actual_hex);
    if (strcmp(actual_hex, expected_hex) != 0) {
        fprintf(stderr, "  FAIL %s\n    expected: %s\n    actual:   %s\n", label, expected_hex, actual_hex);
        return 0;
    }
    return 1;
}

static int test_x3dh_basic(void) {
    printf("Test: signal_fixture_x3dh_basic\n");
    char *inputs_json = load_inputs();
    if (!inputs_json) { fprintf(stderr, "  Cannot load inputs.json\n"); return 0; }
    char *case_obj = find_case(inputs_json, "x3dh_basic");
    if (!case_obj) { fprintf(stderr, "  case x3dh_basic not found\n"); free(inputs_json); return 0; }
    char *expected_json = load_expected("x3dh_basic");
    if (!expected_json) { fprintf(stderr, "  Cannot load expected\n"); free(inputs_json); free(case_obj); return 0; }

    char hex[256];
    uint8_t alice_ik[32], alice_ek[32], bob_ik[32], bob_spk[32], bob_opk[32];

    json_get_str(case_obj, "alice_identity_priv_hex", hex, sizeof(hex));
    hex_decode(hex, alice_ik, 32);
    json_get_str(case_obj, "alice_ephemeral_priv_hex", hex, sizeof(hex));
    hex_decode(hex, alice_ek, 32);
    json_get_str(case_obj, "bob_identity_priv_hex", hex, sizeof(hex));
    hex_decode(hex, bob_ik, 32);
    json_get_str(case_obj, "bob_signed_pre_key_priv_hex", hex, sizeof(hex));
    hex_decode(hex, bob_spk, 32);
    json_get_str(case_obj, "bob_one_time_pre_key_priv_hex", hex, sizeof(hex));
    hex_decode(hex, bob_opk, 32);

    char root_info[64], send_info[64], recv_info[64];
    json_get_str(case_obj, "hkdf_root_info_utf8", root_info, sizeof(root_info));
    json_get_str(case_obj, "hkdf_chain_initiator_send_info_utf8", send_info, sizeof(send_info));
    json_get_str(case_obj, "hkdf_chain_initiator_recv_info_utf8", recv_info, sizeof(recv_info));

    uint8_t alice_ik_pub[32], alice_ek_pub[32], bob_ik_pub[32], bob_spk_pub[32], bob_opk_pub[32];
    aether_x25519_derive_public(alice_ik, alice_ik_pub);
    aether_x25519_derive_public(alice_ek, alice_ek_pub);
    aether_x25519_derive_public(bob_ik, bob_ik_pub);
    aether_x25519_derive_public(bob_spk, bob_spk_pub);
    aether_x25519_derive_public(bob_opk, bob_opk_pub);

    uint8_t dh1[32], dh2[32], dh3[32], dh4[32];
    aether_x25519_agree(alice_ik, bob_spk_pub, dh1);
    aether_x25519_agree(alice_ek, bob_ik_pub, dh2);
    aether_x25519_agree(alice_ek, bob_spk_pub, dh3);
    aether_x25519_agree(alice_ek, bob_opk_pub, dh4);

    uint8_t shared[128];
    memcpy(shared, dh1, 32);
    memcpy(shared + 32, dh2, 32);
    memcpy(shared + 64, dh3, 32);
    memcpy(shared + 96, dh4, 32);

    uint8_t root_key[32], send_chain[32], recv_chain[32];
    aether_hkdf_sha256(NULL, 0, shared, sizeof(shared),
                      (const uint8_t *)root_info, strlen(root_info), 32, root_key);
    aether_hkdf_sha256(NULL, 0, root_key, 32,
                      (const uint8_t *)send_info, strlen(send_info), 32, send_chain);
    aether_hkdf_sha256(NULL, 0, root_key, 32,
                      (const uint8_t *)recv_info, strlen(recv_info), 32, recv_chain);

    // Compare against expected
    int ok = 1;
    char expect[512]; // shared_secret is 128 bytes -> 256 hex chars + NUL
    json_get_str(expected_json, "alice_identity_pub_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("alice_identity_pub_hex", alice_ik_pub, 32, expect);
    json_get_str(expected_json, "alice_ephemeral_pub_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("alice_ephemeral_pub_hex", alice_ek_pub, 32, expect);
    json_get_str(expected_json, "bob_identity_pub_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("bob_identity_pub_hex", bob_ik_pub, 32, expect);
    json_get_str(expected_json, "bob_signed_pre_key_pub_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("bob_signed_pre_key_pub_hex", bob_spk_pub, 32, expect);
    json_get_str(expected_json, "bob_one_time_pre_key_pub_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("bob_one_time_pre_key_pub_hex", bob_opk_pub, 32, expect);
    json_get_str(expected_json, "dh1_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("dh1_hex", dh1, 32, expect);
    json_get_str(expected_json, "dh2_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("dh2_hex", dh2, 32, expect);
    json_get_str(expected_json, "dh3_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("dh3_hex", dh3, 32, expect);
    json_get_str(expected_json, "dh4_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("dh4_hex", dh4, 32, expect);
    json_get_str(expected_json, "shared_secret_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("shared_secret_hex", shared, sizeof(shared), expect);
    json_get_str(expected_json, "root_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("root_key_hex", root_key, 32, expect);
    json_get_str(expected_json, "initiator_send_chain_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("initiator_send_chain_key_hex", send_chain, 32, expect);
    json_get_str(expected_json, "initiator_recv_chain_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("initiator_recv_chain_key_hex", recv_chain, 32, expect);

    free(inputs_json);
    free(case_obj);
    free(expected_json);
    if (ok) printf("  PASS\n");
    return ok;
}

static int test_ratchet_step_basic(void) {
    printf("Test: signal_fixture_ratchet_step_basic\n");
    char *inputs_json = load_inputs();
    char *case_obj = find_case(inputs_json, "ratchet_step_basic");
    char *expected_json = load_expected("ratchet_step_basic");

    char hex[128];
    uint8_t chain_key[32];
    json_get_str(case_obj, "chain_key_hex", hex, sizeof(hex));
    hex_decode(hex, chain_key, 32);

    uint8_t msg_key[32], next_chain[32];
    hmac_one(chain_key, 32, 0x01, msg_key);
    hmac_one(chain_key, 32, 0x02, next_chain);

    int ok = 1;
    char expect[128];
    json_get_str(expected_json, "message_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("message_key_hex", msg_key, 32, expect);
    json_get_str(expected_json, "next_chain_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("next_chain_key_hex", next_chain, 32, expect);

    free(inputs_json); free(case_obj); free(expected_json);
    if (ok) printf("  PASS\n");
    return ok;
}

static int test_ratchet_step_three(void) {
    printf("Test: signal_fixture_ratchet_step_three_iterations\n");
    char *inputs_json = load_inputs();
    char *case_obj = find_case(inputs_json, "ratchet_step_three_iterations");
    char *expected_json = load_expected("ratchet_step_three_iterations");

    char hex[128];
    uint8_t chain_key[32];
    json_get_str(case_obj, "initial_chain_key_hex", hex, sizeof(hex));
    hex_decode(hex, chain_key, 32);

    int ok = 1;
    char expect[128];
    char field[64];
    for (int i = 0; i < 3; i++) {
        uint8_t msg_key[32], next_chain[32];
        hmac_one(chain_key, 32, 0x01, msg_key);
        hmac_one(chain_key, 32, 0x02, next_chain);

        snprintf(field, sizeof(field), "step_%d_message_key_hex", i);
        json_get_str(expected_json, field, expect, sizeof(expect));
        ok &= assert_hex_equal(field, msg_key, 32, expect);

        snprintf(field, sizeof(field), "step_%d_chain_key_after_hex", i);
        json_get_str(expected_json, field, expect, sizeof(expect));
        ok &= assert_hex_equal(field, next_chain, 32, expect);

        memcpy(chain_key, next_chain, 32);
    }

    free(inputs_json); free(case_obj); free(expected_json);
    if (ok) printf("  PASS\n");
    return ok;
}

static int test_kdf_rk_basic(void) {
    printf("Test: signal_fixture_kdf_rk_basic\n");
    char *inputs_json = load_inputs();
    if (!inputs_json) { fprintf(stderr, "  Cannot load inputs.json\n"); return 0; }
    char *case_obj = find_case(inputs_json, "kdf_rk_basic");
    if (!case_obj) { fprintf(stderr, "  case kdf_rk_basic not found\n"); free(inputs_json); return 0; }
    char *expected_json = load_expected("kdf_rk_basic");
    if (!expected_json) { fprintf(stderr, "  Cannot load expected\n"); free(inputs_json); free(case_obj); return 0; }

    char hex[128];
    uint8_t root_key[32], dh_output[32];
    json_get_str(case_obj, "root_key_hex", hex, sizeof(hex));
    hex_decode(hex, root_key, 32);
    json_get_str(case_obj, "dh_output_hex", hex, sizeof(hex));
    hex_decode(hex, dh_output, 32);

    uint8_t new_root[32], new_chain[32];
    if (!aether_signal_kdf_rk(root_key, dh_output, new_root, new_chain)) {
        fprintf(stderr, "  aether_signal_kdf_rk returned false\n");
        free(inputs_json); free(case_obj); free(expected_json);
        return 0;
    }

    int ok = 1;
    char expect[128];
    json_get_str(expected_json, "new_root_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("new_root_key_hex", new_root, 32, expect);
    json_get_str(expected_json, "new_chain_key_hex", expect, sizeof(expect));
    ok &= assert_hex_equal("new_chain_key_hex", new_chain, 32, expect);

    free(inputs_json); free(case_obj); free(expected_json);
    if (ok) printf("  PASS\n");
    return ok;
}

int main(void) {
    if (!find_repo_root()) {
        fprintf(stderr, "Cannot locate repo root (looking for AetherProtocol.slnx).\n");
        return 1;
    }
    printf("Signal fixture verifier — repo root: %s\n", repo_root_path);

    int total = 0, passed = 0;
    total++; if (test_x3dh_basic()) passed++;
    total++; if (test_ratchet_step_basic()) passed++;
    total++; if (test_ratchet_step_three()) passed++;
    total++; if (test_kdf_rk_basic()) passed++;

    printf("\n%d/%d tests passed.\n", passed, total);
    return passed == total ? 0 : 1;
}
