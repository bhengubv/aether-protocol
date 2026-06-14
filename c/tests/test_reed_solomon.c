// SPDX-License-Identifier: MIT
//
// Cross-language vault Reed-Solomon fixture verifier (C).
//
// Proves the C aethernet_reed_solomon codec reproduces
// fixtures/vault/reed_solomon_basic.json byte-for-byte:
//   - every systematic data shard + every Cauchy parity shard matches the fixture,
//   - every recovery subset (systematic fast-path, all-K-data, and a data+parity
//     mix that exercises GF(256) matrix inversion) decodes to the original input,
//   - K-1 survivors is unrecoverable (the should_fail case).
//
// JSON parsing is a tiny hand-rolled extractor (matching the repo idiom). Large hex
// values (the ~2.2 KB input + recovered blobs) are extracted into freshly-allocated
// buffers so there is no fixed-size cap.

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

#include "aethernet/reed_solomon.h"

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

static int json_int(const char *scope, const char *key, int *out) {
    const char *p = find_key(scope, key);
    if (!p) return 0;
    char *end = NULL;
    long v = strtol(p, &end, 10);
    if (end == p) return 0;
    *out = (int)v;
    return 1;
}

// Extract an integer array value for key into a caller buffer. Returns the count.
static int json_int_array(const char *scope, const char *key, int *out, int max) {
    const char *p = find_key(scope, key);
    if (!p || *p != '[') return -1;
    p++;
    int n = 0;
    while (*p && *p != ']') {
        while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r' || *p == ',') p++;
        if (*p == ']' || *p == '\0') break;
        char *end = NULL;
        long v = strtol(p, &end, 10);
        if (end == p) break;
        if (n < max) out[n] = (int)v;
        n++;
        p = end;
    }
    return n;
}

// Returns a malloc'd slice of the i-th brace-delimited object inside the array value
// of array_key, or NULL when out of range.
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
static int g_k = 0, g_m = 0, g_n = 0, g_input_size = 0, g_shard_size = 0;
static uint8_t *g_input = NULL; static size_t g_input_len = 0;
static uint8_t **g_shards = NULL;     // N encoded shards
static size_t g_enc_shard_size = 0;

static int load_fixture(void) {
    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/vault/reed_solomon_basic.json", repo_root_path);
    g_fixture = read_file(path);
    if (!g_fixture) { fprintf(stderr, "  cannot read %s\n", path); return 0; }

    json_int(g_fixture, "k", &g_k);
    json_int(g_fixture, "m", &g_m);
    json_int(g_fixture, "n", &g_n);
    json_int(g_fixture, "input_size", &g_input_size);
    json_int(g_fixture, "shard_size", &g_shard_size);

    char *input_hex = json_str_alloc(g_fixture, "input");
    if (!input_hex) { fprintf(stderr, "  no input\n"); return 0; }
    int ok = hex_decode_alloc(input_hex, &g_input, &g_input_len);
    free(input_hex);
    return ok;
}

static int encode_input(void) {
    aethernet_reed_solomon_t *codec = aethernet_reed_solomon_new(g_k, g_m);
    if (!codec) return 0;
    g_shards = (uint8_t **)calloc((size_t)g_n, sizeof(uint8_t *));
    if (!g_shards) { aethernet_reed_solomon_free(codec); return 0; }
    int ok = aethernet_reed_solomon_encode_data(codec, g_input, g_input_len,
                                                g_shards, &g_enc_shard_size);
    aethernet_reed_solomon_free(codec);
    return ok;
}

// ─── Tests ────────────────────────────────────────────────────────────────

static int test_params(void) {
    printf("Test: rs_fixture_params\n");
    if (g_k != 10 || g_m != 4 || g_n != 14) {
        fprintf(stderr, "  FAIL: K=%d M=%d N=%d (want 10/4/14)\n", g_k, g_m, g_n);
        return 0;
    }
    if ((int)g_input_len != g_input_size) {
        fprintf(stderr, "  FAIL: input size %zu != %d\n", g_input_len, g_input_size);
        return 0;
    }
    if ((int)g_enc_shard_size != g_shard_size) {
        fprintf(stderr, "  FAIL: shard size %zu != %d\n", g_enc_shard_size, g_shard_size);
        return 0;
    }
    printf("  PASS\n");
    return 1;
}

static int test_shard_parity(void) {
    printf("Test: rs_shard_parity\n");
    int ok = 1, n = 0;
    for (int i = 0; ; i++) {
        char *obj = nth_object(g_fixture, "shards", i);
        if (!obj) break;
        n++;
        int idx = -1;
        json_int(obj, "index", &idx);
        char *want_hex = json_str_alloc(obj, "hex");

        uint8_t *want = NULL; size_t want_len = 0;
        if (!want_hex || !hex_decode_alloc(want_hex, &want, &want_len)) {
            fprintf(stderr, "  FAIL shard %d: bad fixture hex\n", idx);
            ok = 0;
        } else if (idx < 0 || idx >= g_n) {
            fprintf(stderr, "  FAIL: shard index %d out of range\n", idx);
            ok = 0;
        } else if (want_len != g_enc_shard_size ||
                   memcmp(g_shards[idx], want, want_len) != 0) {
            fprintf(stderr, "  FAIL shard %d: bytes mismatch\n", idx);
            ok = 0;
        }
        free(want_hex); free(want); free(obj);
    }
    if (n != g_n) { fprintf(stderr, "  FAIL: found %d shards, expected %d\n", n, g_n); ok = 0; }
    if (ok) printf("  PASS (%d shards)\n", n);
    return ok;
}

static int test_recovery_parity(void) {
    printf("Test: rs_recovery_parity\n");
    int ok = 1, n = 0;

    aethernet_reed_solomon_t *codec = aethernet_reed_solomon_new(g_k, g_m);
    if (!codec) { fprintf(stderr, "  codec alloc failed\n"); return 0; }

    for (int i = 0; ; i++) {
        char *obj = nth_object(g_fixture, "recovery", i);
        if (!obj) break;
        n++;

        int survivors[64];
        int sc = json_int_array(obj, "survivor_indices", survivors, 64);
        char *recovered_hex = json_str_alloc(obj, "recovered");

        uint8_t *want = NULL; size_t want_len = 0;
        int parsed = recovered_hex && hex_decode_alloc(recovered_hex, &want, &want_len);

        // Build parallel index/shard arrays for the survivors.
        const uint8_t **avail = (const uint8_t **)malloc((size_t)sc * sizeof(uint8_t *));
        int *idxs = (int *)malloc((size_t)sc * sizeof(int));
        for (int j = 0; j < sc; j++) { idxs[j] = survivors[j]; avail[j] = g_shards[survivors[j]]; }

        uint8_t *out = NULL; size_t out_len = 0;
        bool rec_ok = aethernet_reed_solomon_reconstruct_data(
            codec, idxs, avail, (size_t)sc, g_enc_shard_size, (size_t)g_input_size, &out, &out_len);

        if (!parsed) {
            fprintf(stderr, "  FAIL recovery %d: bad fixture recovered hex\n", i);
            ok = 0;
        } else if (!rec_ok) {
            fprintf(stderr, "  FAIL recovery %d: reconstruct returned false\n", i);
            ok = 0;
        } else if (out_len != want_len || memcmp(out, want, want_len) != 0) {
            fprintf(stderr, "  FAIL recovery %d: recovered bytes != fixture\n", i);
            ok = 0;
        } else if (out_len != g_input_len || memcmp(out, g_input, g_input_len) != 0) {
            fprintf(stderr, "  FAIL recovery %d: recovered != original input\n", i);
            ok = 0;
        }

        free(out); free(want); free(recovered_hex); free(avail); free(idxs); free(obj);
    }

    aethernet_reed_solomon_free(codec);
    if (n == 0) { fprintf(stderr, "  no recovery cases\n"); ok = 0; }
    if (ok) printf("  PASS (%d subsets)\n", n);
    return ok;
}

static int test_k_minus_one_fails(void) {
    printf("Test: rs_k_minus_one_fails\n");
    int ok = 1;

    int survivors[64];
    int sc = json_int_array(g_fixture, "survivor_indices", survivors, 64);
    // The should_fail object is the only top-level "survivor_indices" inside
    // "should_fail"; but find_key finds the first occurrence in document order, which
    // is inside the recovery array. Slice the should_fail object explicitly instead.
    const char *sf = find_key(g_fixture, "should_fail");
    if (sf && *sf == '{') {
        // Find the matching close brace and slice.
        int depth = 0; const char *end = sf;
        for (; *end; end++) {
            if (*end == '{') depth++;
            else if (*end == '}') { depth--; if (depth == 0) { end++; break; } }
        }
        size_t len = (size_t)(end - sf);
        char *slice = (char *)malloc(len + 1);
        memcpy(slice, sf, len); slice[len] = '\0';
        sc = json_int_array(slice, "survivor_indices", survivors, 64);
        free(slice);
    }

    if (sc != g_k - 1) {
        fprintf(stderr, "  FAIL: should_fail must carry K-1=%d survivors, got %d\n", g_k - 1, sc);
        ok = 0;
    }

    aethernet_reed_solomon_t *codec = aethernet_reed_solomon_new(g_k, g_m);
    const uint8_t **avail = (const uint8_t **)malloc((size_t)sc * sizeof(uint8_t *));
    int *idxs = (int *)malloc((size_t)sc * sizeof(int));
    for (int j = 0; j < sc; j++) { idxs[j] = survivors[j]; avail[j] = g_shards[survivors[j]]; }

    uint8_t *out = NULL; size_t out_len = 0;
    bool rec_ok = aethernet_reed_solomon_reconstruct_data(
        codec, idxs, avail, (size_t)sc, g_enc_shard_size, (size_t)g_input_size, &out, &out_len);

    if (rec_ok) {
        fprintf(stderr, "  FAIL: K-1 survivors decoded successfully (must fail)\n");
        ok = 0;
        free(out);
    }

    aethernet_reed_solomon_free(codec);
    free(avail); free(idxs);
    if (ok) printf("  PASS\n");
    return ok;
}

int main(void) {
    if (!find_repo_root()) {
        fprintf(stderr, "Cannot locate repo root (looking for AetherNetProtocol.slnx).\n");
        return 1;
    }
    printf("Vault Reed-Solomon fixture verifier — repo root: %s\n", repo_root_path);
    if (!load_fixture()) { fprintf(stderr, "Cannot load vault fixture.\n"); return 1; }
    if (!encode_input()) { fprintf(stderr, "Encode failed.\n"); return 1; }

    int total = 0, passed = 0;
    total++; if (test_params()) passed++;
    total++; if (test_shard_parity()) passed++;
    total++; if (test_recovery_parity()) passed++;
    total++; if (test_k_minus_one_fails()) passed++;

    if (g_shards) { for (int i = 0; i < g_n; i++) free(g_shards[i]); free(g_shards); }
    free(g_input);
    free(g_fixture);

    printf("\n%d/%d tests passed.\n", passed, total);
    return passed == total ? 0 : 1;
}
