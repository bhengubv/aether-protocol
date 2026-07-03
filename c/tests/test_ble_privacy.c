// SPDX-License-Identifier: MIT
//
// BLE tracking-protection parity gate. Loads fixtures/bleprivacy/vectors.json
// and asserts this C port reproduces the reference
// (src/AetherNet.Security/Privacy/BlePrivacy.cs) byte-for-byte:
//
//   uuid_vectors:  service_uuid(rotation_key, window) == uuid
//   rpa_vectors:   hex(resolvable_address(irk, window)) == rpa
//                  AND resolve_address(irk,       rpa) == true
//                  AND resolve_address(wrong_irk, rpa) == false
//   window_for(899) == 0, window_for(900) == 1
//
// JSON parsing is the same tiny hand-rolled extractor style used by
// test_bip39.c / test_fixtures.c — the schema is flat, no JSON library needed.
// Run from the repo root so the relative fixture path resolves.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/ble_privacy.h"

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

/* ─── hex helpers ───────────────────────────────────────────────────────── */

static int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Hex string -> freshly allocated byte buffer. Caller frees. *out_len set.
static uint8_t *hex_to_bytes(const char *hex, size_t *out_len) {
    size_t hl = strlen(hex);
    size_t n = hl / 2;
    *out_len = n;
    uint8_t *b = (uint8_t *)malloc(n ? n : 1);
    for (size_t i = 0; i < n; i++)
        b[i] = (uint8_t)((hexv(hex[i * 2]) << 4) | hexv(hex[i * 2 + 1]));
    return b;
}

// Lowercase-hex encode bytes into a caller-provided buffer (>= 2*len+1).
static void bytes_to_hex(const uint8_t *b, size_t len, char *out) {
    static const char HEX[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = HEX[(b[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[b[i] & 0xF];
    }
    out[len * 2] = '\0';
}

/* ─── tiny JSON extractor (mirrors test_bip39.c) ────────────────────────── */

static char *read_file(const char *path, size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0) { fclose(f); return NULL; }
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)sz, f);
    buf[got] = '\0';
    fclose(f);
    if (out_len) *out_len = got;
    return buf;
}

// Returns the value of a string field (newly allocated). Caller frees.
static char *json_str_field(const char *obj, const char *key) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(obj, needle);
    if (!p) return NULL;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t')) p++;
    if (*p != '"') return NULL;
    p++;
    const char *start = p;
    while (*p && !(*p == '"' && *(p - 1) != '\\')) p++;
    size_t n = (size_t)(p - start);
    char *out = (char *)malloc(n + 1);
    memcpy(out, start, n);
    out[n] = '\0';
    return out;
}

// Parse an integer field ("key": <int>) out of a flat object. Returns true on
// success, writing the value via *out. The window values here fit in int64.
static bool json_int_field(const char *obj, const char *key, int64_t *out) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(obj, needle);
    if (!p) return false;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t')) p++;
    if (!*p) return false;
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return false;
    *out = (int64_t)v;
    return true;
}

typedef struct {
    int64_t window;
    char *uuid;    // uuid_vectors only
    char *rpa;     // rpa_vectors only (hex)
} ble_vector_t;

// Splits a named "<array_key>" array into flat objects and extracts window +
// the given string field ("uuid" or "rpa") from each. Returns count; writes a
// malloc'd array via *out.
static int parse_vectors(const char *json, const char *array_key,
                         const char *str_key, ble_vector_t **out) {
    char akneedle[64];
    snprintf(akneedle, sizeof(akneedle), "\"%s\"", array_key);
    const char *arr = strstr(json, akneedle);
    if (!arr) return 0;
    const char *p = strchr(arr, '[');
    if (!p) return 0;
    p++;

    int count = 0, cap = 16;
    ble_vector_t *items = (ble_vector_t *)calloc((size_t)cap, sizeof *items);

    while (*p) {
        while (*p && (*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t' || *p == ',')) p++;
        if (!*p || *p == ']') break;
        if (*p != '{') break;
        const char *start = p;
        int depth = 1;
        p++;
        while (*p && depth > 0) {
            if (*p == '{') depth++;
            else if (*p == '}') depth--;
            else if (*p == '"') {
                p++;
                while (*p && *p != '"') {
                    if (*p == '\\' && *(p + 1)) p++;
                    p++;
                }
            }
            p++;
        }
        size_t obj_len = (size_t)(p - start);
        char *obj = (char *)malloc(obj_len + 1);
        memcpy(obj, start, obj_len);
        obj[obj_len] = '\0';

        if (count == cap) {
            cap *= 2;
            items = (ble_vector_t *)realloc(items, (size_t)cap * sizeof *items);
        }
        ble_vector_t *v = &items[count++];
        v->uuid = NULL;
        v->rpa = NULL;
        if (!json_int_field(obj, "window", &v->window))
            FAILF("%s: object missing integer \"window\"", array_key);
        char *sval = json_str_field(obj, str_key);
        if (!sval)
            FAILF("%s: object missing string \"%s\"", array_key, str_key);
        if (strcmp(str_key, "uuid") == 0) v->uuid = sval;
        else                              v->rpa  = sval;
        free(obj);
    }
    *out = items;
    return count;
}

/* ─── checks ────────────────────────────────────────────────────────────── */

static void check_uuid_vectors(const char *json, const uint8_t *rot_key, size_t rot_len) {
    ble_vector_t *v = NULL;
    int n = parse_vectors(json, "uuid_vectors", "uuid", &v);
    if (n == 0) FAILF("parsed zero uuid_vectors");

    for (int i = 0; i < n; i++) {
        char uuid[AETHERNET_BLE_UUID_STR_SIZE];
        if (!aethernet_ble_service_uuid(rot_key, rot_len, v[i].window, uuid, sizeof uuid))
            FAILF("uuid_vectors[%d]: service_uuid failed (window=%lld)",
                  i, (long long)v[i].window);
        if (strcmp(uuid, v[i].uuid) != 0)
            FAILF("uuid_vectors[%d] mismatch (window=%lld)\n  got:  %s\n  want: %s",
                  i, (long long)v[i].window, uuid, v[i].uuid);
        free(v[i].uuid);
        tests_run++;
    }
    free(v);
    printf("  all %d uuid_vectors OK (rotating Service UUID)\n", n);
}

static void check_rpa_vectors(const char *json,
                              const uint8_t *irk, const uint8_t *wrong_irk) {
    ble_vector_t *v = NULL;
    int n = parse_vectors(json, "rpa_vectors", "rpa", &v);
    if (n == 0) FAILF("parsed zero rpa_vectors");

    for (int i = 0; i < n; i++) {
        // 1. hex(resolvable_address(irk, window)) == rpa
        uint8_t rpa[AETHERNET_BLE_RPA_SIZE];
        if (!aethernet_ble_resolvable_address(irk, v[i].window, rpa))
            FAILF("rpa_vectors[%d]: resolvable_address failed (window=%lld)",
                  i, (long long)v[i].window);
        char rpa_hex[AETHERNET_BLE_RPA_SIZE * 2 + 1];
        bytes_to_hex(rpa, sizeof rpa, rpa_hex);
        if (strcmp(rpa_hex, v[i].rpa) != 0)
            FAILF("rpa_vectors[%d] mismatch (window=%lld)\n  got:  %s\n  want: %s",
                  i, (long long)v[i].window, rpa_hex, v[i].rpa);

        // 2. resolve_address(irk, rpa) == true  (the owner recognises its RPA)
        if (!aethernet_ble_resolve_address(irk, AETHERNET_BLE_IRK_SIZE,
                                           rpa, sizeof rpa))
            FAILF("rpa_vectors[%d]: resolve_address(irk, rpa) returned false", i);

        // 3. resolve_address(wrong_irk, rpa) == false  (a stranger cannot link it)
        if (aethernet_ble_resolve_address(wrong_irk, AETHERNET_BLE_IRK_SIZE,
                                          rpa, sizeof rpa))
            FAILF("rpa_vectors[%d]: resolve_address(wrong_irk, rpa) returned true", i);

        free(v[i].rpa);
        tests_run++;
    }
    free(v);
    printf("  all %d rpa_vectors OK (RPA hex + resolve true / wrong_irk false)\n", n);
}

static void check_window_for(void) {
    if (aethernet_ble_window_for(899) != 0)
        FAILF("window_for(899) != 0 (got %lld)",
              (long long)aethernet_ble_window_for(899));
    if (aethernet_ble_window_for(900) != 1)
        FAILF("window_for(900) != 1 (got %lld)",
              (long long)aethernet_ble_window_for(900));
    printf("  window_for boundary OK (899->0, 900->1)\n");
    tests_run++;
}

/* ─── guard-path sanity (not fixture-driven) ────────────────────────────── */

static void check_guards(void) {
    uint8_t irk[AETHERNET_BLE_IRK_SIZE] = {0};
    uint8_t rpa[AETHERNET_BLE_RPA_SIZE] = {0};
    char uuid[AETHERNET_BLE_UUID_STR_SIZE];
    uint8_t rot_key[1] = {0};

    // NULL / short-buffer guards must fail cleanly, not crash.
    if (aethernet_ble_service_uuid(NULL, 1, 0, uuid, sizeof uuid))
        FAILF("guard: service_uuid accepted NULL rotation_key");
    if (aethernet_ble_service_uuid(rot_key, 1, 0, uuid, 10))
        FAILF("guard: service_uuid accepted an undersized out buffer");
    if (aethernet_ble_resolvable_address(NULL, 0, rpa))
        FAILF("guard: resolvable_address accepted NULL irk");
    if (aethernet_ble_resolvable_address(irk, 0, NULL))
        FAILF("guard: resolvable_address accepted NULL out_rpa");

    // Wrong lengths -> resolve is false, never a crash.
    if (aethernet_ble_resolve_address(irk, 15, rpa, sizeof rpa))
        FAILF("guard: resolve_address accepted a 15-byte irk");
    if (aethernet_ble_resolve_address(irk, AETHERNET_BLE_IRK_SIZE, rpa, 5))
        FAILF("guard: resolve_address accepted a 5-byte rpa");
    if (aethernet_ble_resolve_address(NULL, AETHERNET_BLE_IRK_SIZE, rpa, sizeof rpa))
        FAILF("guard: resolve_address accepted NULL irk");

    printf("  guard-paths OK (NULL / wrong-length rejected)\n");
    tests_run++;
}

int main(void) {
    printf("AetherNet BLE Tracking-Protection Parity (C)\n");
    printf("============================================\n");

    const char *candidates[] = {
        "fixtures/bleprivacy/vectors.json",
        "../fixtures/bleprivacy/vectors.json",
        "../../fixtures/bleprivacy/vectors.json",
        "../../../fixtures/bleprivacy/vectors.json",
        NULL,
    };
    char *json = NULL;
    for (int i = 0; candidates[i]; i++) {
        json = read_file(candidates[i], NULL);
        if (json) break;
    }
    if (!json) FAILF("could not locate fixtures/bleprivacy/vectors.json (run from repo root)");

    char *rot_hex = json_str_field(json, "rotation_key");
    char *irk_hex = json_str_field(json, "irk");
    char *wrong_hex = json_str_field(json, "wrong_irk");
    if (!rot_hex)   FAILF("vectors.json missing top-level \"rotation_key\"");
    if (!irk_hex)   FAILF("vectors.json missing top-level \"irk\"");
    if (!wrong_hex) FAILF("vectors.json missing top-level \"wrong_irk\"");

    size_t rot_len = 0, irk_len = 0, wrong_len = 0;
    uint8_t *rot_key = hex_to_bytes(rot_hex, &rot_len);
    uint8_t *irk = hex_to_bytes(irk_hex, &irk_len);
    uint8_t *wrong_irk = hex_to_bytes(wrong_hex, &wrong_len);
    if (irk_len != AETHERNET_BLE_IRK_SIZE)   FAILF("irk is not 16 bytes (got %zu)", irk_len);
    if (wrong_len != AETHERNET_BLE_IRK_SIZE) FAILF("wrong_irk is not 16 bytes (got %zu)", wrong_len);

    printf("Loaded fixtures (rotation_key=%zuB, irk=%zuB).\n", rot_len, irk_len);

    check_uuid_vectors(json, rot_key, rot_len);
    check_rpa_vectors(json, irk, wrong_irk);
    check_window_for();
    check_guards();

    free(rot_key);
    free(irk);
    free(wrong_irk);
    free(rot_hex);
    free(irk_hex);
    free(wrong_hex);
    free(json);

    printf("\n%d BLE privacy checks passed.\n", tests_run);
    return 0;
}
